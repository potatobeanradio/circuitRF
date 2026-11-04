// brief-em3d-28 R-em3d28-1b — route A on Linux: native Vulkan through Vortice.Vulkan's MANAGED bindings
// (MIT; the loader, libvulkan.so.1, is the system's or the driver's). No native package (R-em3d28-1c).
//
// Lifted from tools/Viewer3dSpike's Vulkan half (brief 28 step 0, findings §8), whose harness passed on
// Linux arm64 under Mesa's software driver (0 B per orbit frame, 0 B managed allocation per frame, the
// pick reads back the right object, and its frame matches Metal's pixel for pixel to a mean 0.095).
// What the presentation path must be was read from Avalonia 12.0.3's own importers, not assumed:
//   * The image is exported as an OPAQUE POSIX FD. Avalonia's Vulkan compositor RE-CREATES the image
//     from it with fixed parameters — R8G8B8A8, optimal tiling, usage TRANSFER_SRC | TRANSFER_DST |
//     SAMPLED | COLOR_ATTACHMENT, MUTABLE_FORMAT, one mip, a DEDICATED allocation whose size must equal
//     MemorySize — and an opaque handle only imports into an identically-created image, so ours is made
//     exactly so.
//   * Both importers assume the image is in TRANSFER_SRC_OPTIMAL when their wait completes (the Vulkan
//     one transitions to it once and reads from it; the GLX one waits its semaphore with
//     GL_LAYOUT_TRANSFER_SRC_EXT). So every frame's render pass ends in TRANSFER_SRC_OPTIMAL.
//   * Synchronisation is BINARY semaphores, a pair per image: "ready" (we signal it when the frame is
//     done) and "released" (the compositor signals it when it has read the image). A binary semaphore
//     may be waited only after its signal was SUBMITTED, so before the GPU waits "released" the render
//     thread sees the compositor's update task complete; one that has not is a fault, never a loop.
//   * Each import takes ownership of its fd, so a fresh fd is exported for every import.
//   * The GLX compositor (Avalonia's Linux default) offers these handles only when the GL driver has
//     BOTH GL_EXT_memory_object_fd and GL_EXT_semaphore_fd; Mesa's software GL has only the first, and
//     Avalonia refuses it for GLX anyway — a software stack cannot host this pane.
//   * The device is matched to the compositor's by UUID (ICompositionGpuInterop.DeviceUuid).
//
// The shader is the generated scene.spv (Shaders/): one module, four entry points. tools/ShaderGen
// writes it with naga's ADJUST_COORDINATE_SPACE, which negates clip-space y in the vertex shader — so
// the image comes out upright with the SAME view-projection the Metal and D3D11 backends use. FlipY is
// therefore FALSE here, and a triangle keeps the on-screen winding it has on the other two APIs, so
// the front face is COUNTER-CLOCKWISE as it is there (the shader's front_facing draws the clip caps).
//
// Per frame the 400-byte uniform block (brief 29 added the field block) is copied into a persistently-mapped, host-coherent ring and
// bound as a dynamic uniform buffer — counted as uniform bytes, never geometry. Three frames in flight,
// each with its own command buffer, fence and 1×1 pick readback buffer, read when its fence has
// signalled, never waited for.

using System.Numerics;
using System.Runtime.InteropServices;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Fields;
using CircuitRF.Render.Scene3D.Look;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace CircuitRF.Ui.Viewer3D.Vulkan;

internal sealed unsafe class VulkanViewer3DBackend : Viewer3DBackend
{
    private const int Ring = 3;

    /// <summary>How long a frame's fence may take before the GPU is declared stuck (a fault, never an
    /// endless wait: the render thread holds the render lock, and the UI thread takes it on detach).</summary>
    private const ulong FenceTimeoutNs = 2_000_000_000;
    /// <summary>brief-em3d-46 — one per-draw transform slot in the transform buffer: 80 bytes (brief-em3d-48: the matrix and the id offset), at an offset every
    /// minUniformBufferOffsetAlignment divides (the spec caps that at 256).</summary>
    private const int TransformStride = 256;
    /// <summary>Transform slots per frame slot: a preview's copies at first; brief-em3d-48's array elements (a slot each,
    /// and one for each one's box) grow it — the ring is re-made, once, between frames (EnsureTransformRing).</summary>
    private int _transformSlots = 1 + Scene3DFramePlan.MaxPreviewCopies;
    // ≥ the uniform block (2,016 bytes since brief-em3d-96's four field blocks) and a multiple of every minUniformBufferOffsetAlignment (≤ 256)
    private const int UniformStride = (Scene3DFramePlan.UniformBytes + 255) / 256 * 256;
    private const VkFormat ColorFormat = VkFormat.R8G8B8A8Unorm;
    private const VkFormat DepthFormat = VkFormat.D32Sfloat;
    private const VkImageUsageFlags TargetUsage =
        VkImageUsageFlags.TransferSrc | VkImageUsageFlags.TransferDst | VkImageUsageFlags.Sampled | VkImageUsageFlags.ColorAttachment;
    private const string FdHandle = KnownPlatformGraphicsExternalImageHandleTypes.VulkanOpaquePosixFileDescriptor;
    private const string FdSemaphore = KnownPlatformGraphicsExternalSemaphoreHandleTypes.VulkanOpaquePosixFileDescriptor;

    private VkInstance _instance;
    private VkInstanceApi? _vi;
    private VkPhysicalDevice _physical;
    private VkDevice _device;
    private VkDeviceApi? _api;
    private VkQueue _queue;
    private uint _queueFamily;
    private byte[] _deviceUuid = [];
    private bool _canExport;
    /// <summary>Whether the device enabled <c>depthBiasClamp</c>: what lies on a face has its bias clamped (Scene3DFramePlan.DepthBias), and
    /// without the feature a clamp must be 0 — such a draw then takes no slope factor, the constant alone as before.</summary>
    private bool _biasClamp;
    private VkPhysicalDeviceMemoryProperties _mem;
    private string _description = "Vulkan (no device yet)";

    private VkRenderPass _rpColor, _rpPick;
    private VkDescriptorSetLayout _setLayout;
    private VkPipelineLayout _layout;
    private VkDescriptorPool _pool;
    private VkDescriptorSet _set;
    private VkShaderModule _module;
    private VkPipeline _pOpaque, _pTrans, _pLines, _pPick, _pField, _pEdges, _pTop, _pGrid;
    /// <summary>brief-em3d-101 — the image pipelines, the image vertex stream, the one sampler, and set 1's layout: a sampled image
    /// (binding 0) and a sampler (binding 1). Each texture owns its own one-set descriptor pool (<see cref="Texture"/>), so a
    /// re-elaboration can never exhaust a shared pool: the set lives and dies with its texture (src/Ui/Viewer3D/RESOLVED.md).</summary>
    private VkPipeline _pImage, _pImageTrans;
    private VkDescriptorSetLayout _imageSetLayout;
    private VkSampler _sampler;
    private (VkBuffer Buf, VkDeviceMemory Mem) _imageVb;
    private readonly Scene3DTextureResidency<Texture?> _textures = new();

    /// <summary>brief-em3d-101 — one uploaded image: its image, memory and view, and the descriptor set (in a pool of its own) that
    /// binds it at set 1.</summary>
    private sealed class Texture
    {
        public VkImage Image;
        public VkDeviceMemory Memory;
        public VkImageView View;
        public VkDescriptorPool Pool;
        public VkDescriptorSet Set;
    }
    private VkCommandPool _cmdPool;
    private (VkBuffer Buf, VkDeviceMemory Mem) _ub;
    /// <summary>brief-em3d-46 — the per-draw transforms (binding 1), <see cref="_transformSlots"/> per frame slot.</summary>
    private (VkBuffer Buf, VkDeviceMemory Mem) _tb;
    private byte* _tMapped;
    private byte* _uMapped;
    private readonly VkCommandBuffer[] _cmd = new VkCommandBuffer[Ring];
    private readonly VkFence[] _fence = new VkFence[Ring];
    private readonly bool[] _submitted = new bool[Ring];
    private readonly (VkBuffer Buf, VkDeviceMemory Mem)[] _pickBuf = new (VkBuffer, VkDeviceMemory)[Ring];
    private readonly byte*[] _pickMapped = new byte*[Ring];
    private readonly bool[] _pickPending = new bool[Ring];
    private readonly long[] _pickFrame = new long[Ring];
    private Target _pickId = null!, _pickPos = null!, _pickDepth = null!;
    private VkFramebuffer _pickFb;
    private long _frame;
    private (VkBuffer Buf, VkDeviceMemory Mem) _vb, _ib, _lines, _field;
    /// <summary>brief-em3d-104 — the shade stream (Scene3DShadeVertex), held only while the realistic view is on. Vertex binding 1
    /// of the realistic pipelines (brief 106; scene.wgsl's header has the mapping).</summary>
    private (VkBuffer Buf, VkDeviceMemory Mem) _shade;
    /// <summary>brief-em3d-106 — the realistic pipelines (opaque, premultiplied translucent, the backdrop); set 2's layout (the
    /// environment's map, its sampler, the split-sum table); the environment's two RGBA16F textures and their set, held only while the
    /// realistic view is on; and the appearance table at set 0 binding 2 — a 12 KB host-visible buffer made with the device, so the
    /// descriptor set 0 always holds is always valid.</summary>
    private VkPipeline _pPbr, _pPbrTrans, _pBackdrop;
    // brief-em3d-109 — the blended field (Exact/Glow below 100 %), the Lit field, and the Lit field's normal stream (vertex binding 1)
    private VkPipeline _pFieldBlend, _pFieldLit;
    private (VkBuffer Buf, VkDeviceMemory Mem) _fieldNormals;
    private int _fieldNormalCount;
    private VkDescriptorSetLayout _envSetLayout;
    private Texture? _envMap, _envBrdf;
    private VkDescriptorPool _envPool;
    private VkDescriptorSet _envSet;
    private (VkBuffer Buf, VkDeviceMemory Mem) _apb;
    private byte* _apMapped;
    private bool _appearancesWritten;
    /// <summary>brief-em3d-107 — shadows, occlusion and the ground. Three render passes (the shadow map's, depth only; the occlusion's
    /// prepass, R32 float depths plus a depth buffer of its own; its horizon pass and blur, R8), their pipelines and the ground's and the
    /// glass's; set 3's layout (the shadow map, its comparison sampler, the blurred occlusion, the prepass's depths, the raw occlusion) and
    /// its one set, which always names valid images — 1 × 1 stand-ins, made with the device, until the real ones exist. Re-pointing it
    /// waits for the device, which happens only when a map or a target is made (a size change), never per frame.</summary>
    private VkRenderPass _rpShadow, _rpPrepass, _rpAo;
    private VkPipeline _pShadow, _pPrepass, _pGroundPrepass, _pAo, _pAoBlur, _pGround, _pPbrGlass;
    private VkDescriptorSetLayout _lightSetLayout;
    private VkDescriptorPool _lightPool;
    private VkDescriptorSet _lightSet;
    private VkSampler _shadowSampler;
    /// <summary>Whether the device filters a D32 float image linearly: the comparison sampler then blends four comparisons a tap.</summary>
    private bool _depthLinear;
    /// <summary>Issue #20 — the images the compositor imports are LINEAR on an AMD device. Avalonia's GL import never marks the
    /// memory object dedicated, and Mesa's AMD GL driver then reads it as linear whatever RADV drew it as: an optimal image comes
    /// out as horizontal streaks. Linear here makes the two agree; nothing else this backend draws into changes.</summary>
    private bool _linearShared;
    private Target? _noShadowMap, _noOcclusion;
    private ShadowMap? _shadowMap;
    private OcclusionTargets? _occlusion;

    private sealed class ShadowMap
    {
        public Target Depth = null!;
        public VkFramebuffer Framebuffer;
        public int Size;
    }

    /// <summary>The occlusion's targets at one size: the prepass's depths and its own depth buffer, the raw occlusion and the blurred one.</summary>
    private sealed class OcclusionTargets
    {
        public Target Depths = null!, DepthBuffer = null!, Raw = null!, Blur = null!;
        public VkFramebuffer Prepass, AoPass, BlurPass;
        public int Width, Height;
    }
    private int _fieldCount;
    private readonly (VkBuffer Buf, VkDeviceMemory Mem)[] _overlays = new (VkBuffer, VkDeviceMemory)[3];

    /// <summary>An image with its memory and view.</summary>
    private sealed class Target
    {
        public VkImage Image;
        public VkDeviceMemory Memory;
        public ulong MemorySize;
        public VkImageView View;
    }

    private sealed class Image
    {
        public Target Color = null!, Depth = null!;
        public VkFramebuffer Framebuffer;
        public int Width, Height;
        public VkSemaphore Ready, Released;
        public ICompositionImportedGpuImage? Imported;
        public ICompositionImportedGpuSemaphore? ReadyImported, ReleasedImported;
        public Task? Pending;
        /// <summary>The compositor was handed this image; its "released" signal is owed to our next frame.</summary>
        public bool ReleaseOwed;
        /// <summary>"ready" was signalled by a frame that has not been presented yet.</summary>
        public bool ReadySignalled;
    }
    private Image[] _images = [];

    public override string Description => _description;

    private VkDeviceApi Api => _api ?? CreateDevice(null);

    // ── device ──────────────────────────────────────────────────────────────────────────────

    private VkDeviceApi CreateDevice(byte[]? uuid)
    {
        if (vkInitialize() != VkResult.Success)
            throw new Viewer3DPresentFault("This machine has no Vulkan loader (libvulkan.so.1).");
        // An earlier attempt that failed after making its instance left it here: never leak one per try.
        _vi?.vkDestroyInstance(null);
        _vi = null;
        var app = new VkApplicationInfo { apiVersion = VkVersion.Version_1_1 };
        fixed (byte* name = "circuitRF 3D view"u8) app.pApplicationName = name;
        var ici = new VkInstanceCreateInfo { pApplicationInfo = &app };
        VkInstance inst;
        Check(vkCreateInstance(&ici, &inst), "vkCreateInstance");
        _instance = inst;
        var vi = GetApi(inst);
        _vi = vi;

        uint n = 0;
        Check(vi.vkEnumeratePhysicalDevices(&n, null), "vkEnumeratePhysicalDevices");
        var devs = new VkPhysicalDevice[n];
        fixed (VkPhysicalDevice* pd = devs) Check(vi.vkEnumeratePhysicalDevices(&n, pd), "vkEnumeratePhysicalDevices");
        var seen = new List<string>();
        foreach (var dev in devs)
        {
            var id = new VkPhysicalDeviceIDProperties();
            var p2 = new VkPhysicalDeviceProperties2 { pNext = &id };
            vi.vkGetPhysicalDeviceProperties2(dev, &p2);
            var u = new ReadOnlySpan<byte>(id.deviceUUID, 16).ToArray();
            seen.Add(Convert.ToHexString(u));
            if (uuid is { Length: 16 } && !u.AsSpan().SequenceEqual(uuid)) continue;
            uint qn = 0;
            vi.vkGetPhysicalDeviceQueueFamilyProperties(dev, &qn, null);
            var qf = new VkQueueFamilyProperties[qn];
            fixed (VkQueueFamilyProperties* pq = qf) vi.vkGetPhysicalDeviceQueueFamilyProperties(dev, &qn, pq);
            int family = Array.FindIndex(qf, q => (q.queueFlags & VkQueueFlags.Graphics) != 0);
            if (family < 0) continue;
            _physical = dev;
            _queueFamily = (uint)family;
            _deviceUuid = u;
            _description = "Vulkan — " + Marshal.PtrToStringUTF8((nint)p2.properties.deviceName);
            break;
        }
        if (_physical.Handle == 0)
            throw new Viewer3DPresentFault(uuid is { Length: 16 }
                ? $"No Vulkan device has the compositor's UUID {Convert.ToHexString(uuid)} (devices: {string.Join(", ", seen)})."
                : "No Vulkan device has a graphics queue.");

        uint en = 0;
        vi.vkEnumerateDeviceExtensionProperties(_physical, null, &en, null);
        var ext = new VkExtensionProperties[en];
        fixed (VkExtensionProperties* pe = ext) vi.vkEnumerateDeviceExtensionProperties(_physical, null, &en, pe);
        bool Has(string e) { foreach (var x in ext) if (Marshal.PtrToStringUTF8((nint)x.extensionName) == e) return true; return false; }
        _canExport = Has("VK_KHR_external_memory_fd") && Has("VK_KHR_external_semaphore_fd");

        VkFormatProperties depthProps;
        vi.vkGetPhysicalDeviceFormatProperties(_physical, DepthFormat, &depthProps);
        _depthLinear = (depthProps.optimalTilingFeatures & VkFormatFeatureFlags.SampledImageFilterLinear) != 0;
        // The compositor is GL on X11 (Avalonia's default, GLX then software; circuitRF never asks for its Vulkan mode, whose
        // importer re-creates the image OPTIMAL and would then disagree with a linear one).
        VkPhysicalDeviceProperties props;
        vi.vkGetPhysicalDeviceProperties(_physical, &props);
        VkFormatProperties colorProps;
        vi.vkGetPhysicalDeviceFormatProperties(_physical, ColorFormat, &colorProps);
        const VkFormatFeatureFlags linearNeeds = VkFormatFeatureFlags.ColorAttachment | VkFormatFeatureFlags.ColorAttachmentBlend |
                                                 VkFormatFeatureFlags.SampledImage | VkFormatFeatureFlags.TransferSrc | VkFormatFeatureFlags.TransferDst;
        _linearShared = props.vendorID == 0x1002 && (colorProps.linearTilingFeatures & linearNeeds) == linearNeeds;
        VkPhysicalDeviceFeatures supported;
        vi.vkGetPhysicalDeviceFeatures(_physical, &supported);
        _biasClamp = supported.depthBiasClamp;
        var features = new VkPhysicalDeviceFeatures { depthBiasClamp = _biasClamp };

        float prio = 1f;
        var qci = new VkDeviceQueueCreateInfo { queueFamilyIndex = _queueFamily, queueCount = 1, pQueuePriorities = &prio };
        fixed (byte* e1 = "VK_KHR_external_memory_fd"u8)
        fixed (byte* e2 = "VK_KHR_external_semaphore_fd"u8)
        {
            byte** names = stackalloc byte*[2] { e1, e2 };
            var dci = new VkDeviceCreateInfo
            {
                queueCreateInfoCount = 1, pQueueCreateInfos = &qci,
                enabledExtensionCount = _canExport ? 2u : 0u, ppEnabledExtensionNames = names,
                pEnabledFeatures = &features,
            };
            VkDevice device;
            Check(vi.vkCreateDevice(_physical, &dci, null, &device), "vkCreateDevice");
            _device = device;
        }
        var api = GetApi(inst, _device);
        _api = api;
        VkQueue queue;
        api.vkGetDeviceQueue(_queueFamily, 0, &queue);
        if (queue.Handle == 0) throw new Viewer3DPresentFault("Vulkan returned no queue.");
        _queue = queue;
        VkPhysicalDeviceMemoryProperties mp;
        vi.vkGetPhysicalDeviceMemoryProperties(_physical, &mp);
        _mem = mp;
        BuildPipelines(api);
        return api;
    }

    private static void Check(VkResult r, string what)
    {
        if (r != VkResult.Success) throw new Viewer3DPresentFault($"{what} returned {r}.");
    }

    private uint MemoryType(uint bits, VkMemoryPropertyFlags want)
    {
        for (uint i = 0; i < _mem.memoryTypeCount; i++)
            if ((bits & (1u << (int)i)) != 0 && (_mem.memoryTypes[(int)i].propertyFlags & want) == want) return i;
        throw new Viewer3DPresentFault($"Vulkan has no {want} memory type for the 3D view.");
    }

    private void BuildPipelines(VkDeviceApi api)
    {
        var cpi = new VkCommandPoolCreateInfo { flags = VkCommandPoolCreateFlags.ResetCommandBuffer, queueFamilyIndex = _queueFamily };
        VkCommandPool cmdPool;
        Check(api.vkCreateCommandPool(&cpi, null, &cmdPool), "vkCreateCommandPool");
        _cmdPool = cmdPool;

        var spirv = Viewer3DShaders.Spirv;
        fixed (byte* code = spirv)
        {
            var smi = new VkShaderModuleCreateInfo { codeSize = (nuint)spirv.Length, pCode = (uint*)code };
            VkShaderModule sm;
            Check(api.vkCreateShaderModule(&smi, null, &sm), "vkCreateShaderModule");
            _module = sm;
        }

        _rpColor = RenderPass(api, pick: false);
        _rpPick = RenderPass(api, pick: true);
        _rpShadow = ReadBackPass(api, VkFormat.Undefined, depth: true);
        _rpPrepass = ReadBackPass(api, VkFormat.R32Sfloat, depth: true);
        _rpAo = ReadBackPass(api, VkFormat.R8Unorm, depth: false);

        var bindings = stackalloc VkDescriptorSetLayoutBinding[3];
        bindings[0] = new VkDescriptorSetLayoutBinding
        {
            binding = 0, descriptorType = VkDescriptorType.UniformBufferDynamic, descriptorCount = 1,
            stageFlags = VkShaderStageFlags.Vertex | VkShaderStageFlags.Fragment,
        };
        // brief-em3d-46 — the per-draw transform: a second dynamic uniform, re-offset per draw that changes it.
        bindings[1] = new VkDescriptorSetLayoutBinding
        {
            binding = 1, descriptorType = VkDescriptorType.UniformBufferDynamic, descriptorCount = 1,
            stageFlags = VkShaderStageFlags.Vertex,
        };
        // brief-em3d-106 — the appearance table: a plain (not dynamic) uniform the realistic fragment reads.
        bindings[2] = new VkDescriptorSetLayoutBinding
        {
            binding = 2, descriptorType = VkDescriptorType.UniformBuffer, descriptorCount = 1, stageFlags = VkShaderStageFlags.Fragment,
        };
        var dsl = new VkDescriptorSetLayoutCreateInfo { bindingCount = 3, pBindings = bindings };
        VkDescriptorSetLayout setLayout;
        Check(api.vkCreateDescriptorSetLayout(&dsl, null, &setLayout), "vkCreateDescriptorSetLayout");
        _setLayout = setLayout;
        // brief-em3d-101 — set 1: an image draw's texture and its sampler (tools/ShaderGen's binding map).
        var ib = stackalloc VkDescriptorSetLayoutBinding[2];
        ib[0] = new VkDescriptorSetLayoutBinding { binding = 0, descriptorType = VkDescriptorType.SampledImage, descriptorCount = 1, stageFlags = VkShaderStageFlags.Fragment };
        ib[1] = new VkDescriptorSetLayoutBinding { binding = 1, descriptorType = VkDescriptorType.Sampler, descriptorCount = 1, stageFlags = VkShaderStageFlags.Fragment };
        var idsl = new VkDescriptorSetLayoutCreateInfo { bindingCount = 2, pBindings = ib };
        VkDescriptorSetLayout imageLayout;
        Check(api.vkCreateDescriptorSetLayout(&idsl, null, &imageLayout), "vkCreateDescriptorSetLayout");
        _imageSetLayout = imageLayout;
        // brief-em3d-106 — set 2: the environment's map, its sampler and the split-sum table (tools/ShaderGen's binding map).
        var eb = stackalloc VkDescriptorSetLayoutBinding[3];
        eb[0] = new VkDescriptorSetLayoutBinding { binding = 0, descriptorType = VkDescriptorType.SampledImage, descriptorCount = 1, stageFlags = VkShaderStageFlags.Fragment };
        eb[1] = new VkDescriptorSetLayoutBinding { binding = 1, descriptorType = VkDescriptorType.Sampler, descriptorCount = 1, stageFlags = VkShaderStageFlags.Fragment };
        eb[2] = new VkDescriptorSetLayoutBinding { binding = 2, descriptorType = VkDescriptorType.SampledImage, descriptorCount = 1, stageFlags = VkShaderStageFlags.Fragment };
        var edsl = new VkDescriptorSetLayoutCreateInfo { bindingCount = 3, pBindings = eb };
        VkDescriptorSetLayout envLayout;
        Check(api.vkCreateDescriptorSetLayout(&edsl, null, &envLayout), "vkCreateDescriptorSetLayout");
        _envSetLayout = envLayout;
        // brief-em3d-107 — set 3: the shadow map, its comparison sampler, the blurred occlusion, the prepass's depths, the raw occlusion.
        var lb = stackalloc VkDescriptorSetLayoutBinding[5];
        for (uint k = 0; k < 5; k++)
            lb[k] = new VkDescriptorSetLayoutBinding
            {
                binding = k, descriptorType = k == 1 ? VkDescriptorType.Sampler : VkDescriptorType.SampledImage, descriptorCount = 1,
                stageFlags = VkShaderStageFlags.Fragment,
            };
        var ldsl = new VkDescriptorSetLayoutCreateInfo { bindingCount = 5, pBindings = lb };
        VkDescriptorSetLayout lightLayout;
        Check(api.vkCreateDescriptorSetLayout(&ldsl, null, &lightLayout), "vkCreateDescriptorSetLayout");
        _lightSetLayout = lightLayout;
        var both = stackalloc VkDescriptorSetLayout[4] { setLayout, imageLayout, envLayout, lightLayout };
        var pli = new VkPipelineLayoutCreateInfo { setLayoutCount = 4, pSetLayouts = both };
        VkPipelineLayout layout;
        Check(api.vkCreatePipelineLayout(&pli, null, &layout), "vkCreatePipelineLayout");
        _layout = layout;

        _pOpaque = Pipeline(api, _rpColor, "fs_color"u8, VkPrimitiveTopology.TriangleList, blend: false, depthWrite: true, targets: 1);
        _pTrans = Pipeline(api, _rpColor, "fs_color"u8, VkPrimitiveTopology.TriangleList, blend: true, depthWrite: false, targets: 1);
        _pLines = Pipeline(api, _rpColor, "fs_line"u8, VkPrimitiveTopology.LineList, blend: false, depthWrite: true, targets: 1);
        _pPick = Pipeline(api, _rpPick, "fs_pick"u8, VkPrimitiveTopology.TriangleList, blend: false, depthWrite: true, targets: 2);
        _pField = Pipeline(api, _rpColor, "fs_field"u8, VkPrimitiveTopology.TriangleList, blend: false, depthWrite: true, targets: 1, field: true);
        _pFieldBlend = Pipeline(api, _rpColor, "fs_field"u8, VkPrimitiveTopology.TriangleList, blend: true, depthWrite: true, targets: 1, field: true);
        _pFieldLit = Pipeline(api, _rpColor, "fs_field_lit"u8, VkPrimitiveTopology.TriangleList, blend: true, depthWrite: true, targets: 1,
                              field: true, fieldLit: true);
        // brief-em3d-43 — the selection's edges and its face on top: no depth test.
        _pEdges = Pipeline(api, _rpColor, "fs_edge"u8, VkPrimitiveTopology.LineList, blend: true, depthWrite: false, targets: 1, depthTest: false);
        _pTop = Pipeline(api, _rpColor, "fs_top"u8, VkPrimitiveTopology.TriangleList, blend: true, depthWrite: false, targets: 1, depthTest: false);
        // brief-em3d-45 — the drawing grid: no vertex input at all, depth test without write, blend.
        _pGrid = Pipeline(api, _rpColor, "fs_grid"u8, VkPrimitiveTopology.TriangleList, blend: true, depthWrite: false, targets: 1, grid: true);
        // brief-em3d-101 — an image: blended always (an opaque image's alpha is 1); the opaque one writes depth.
        _pImage = Pipeline(api, _rpColor, "fs_image"u8, VkPrimitiveTopology.TriangleList, blend: true, depthWrite: true, targets: 1, image: true);
        _pImageTrans = Pipeline(api, _rpColor, "fs_image"u8, VkPrimitiveTopology.TriangleList, blend: true, depthWrite: false, targets: 1, image: true);
        // brief-em3d-106 — the realistic view: the scene's vertex (binding 0) and the shade stream (binding 1); the translucent one
        // blends premultiplied; the backdrop is vs_grid's six vertices with no depth at all.
        _pPbr = Pipeline(api, _rpColor, "fs_pbr"u8, VkPrimitiveTopology.TriangleList, blend: false, depthWrite: true, targets: 1, pbr: true);
        _pPbrTrans = Pipeline(api, _rpColor, "fs_pbr"u8, VkPrimitiveTopology.TriangleList, blend: true, depthWrite: false, targets: 1, pbr: true,
                              premultiplied: true);
        // brief-em3d-107 — the glass (fs_pbr_glass: no occlusion), the shadow map's casters (vs_shadow, depth only), the occlusion's
        // prepass (the materials through vs, the ground through vs_grid), its horizon pass and blur (no depth), and the ground.
        _pPbrGlass = Pipeline(api, _rpColor, "fs_pbr_glass"u8, VkPrimitiveTopology.TriangleList, blend: true, depthWrite: false, targets: 1,
                              pbr: true, premultiplied: true);
        _pShadow = Pipeline(api, _rpShadow, "fs_depth"u8, VkPrimitiveTopology.TriangleList, blend: false, depthWrite: true, targets: 0,
                            vertexEntry: "vs_shadow"u8);
        _pPrepass = Pipeline(api, _rpPrepass, "fs_prepass"u8, VkPrimitiveTopology.TriangleList, blend: false, depthWrite: true, targets: 1);
        _pGroundPrepass = Pipeline(api, _rpPrepass, "fs_ground_prepass"u8, VkPrimitiveTopology.TriangleList, blend: false, depthWrite: true,
                                   targets: 1, grid: true);
        _pAo = Pipeline(api, _rpAo, "fs_ao"u8, VkPrimitiveTopology.TriangleList, blend: false, depthWrite: false, targets: 1, depthTest: false, grid: true);
        _pAoBlur = Pipeline(api, _rpAo, "fs_ao_blur"u8, VkPrimitiveTopology.TriangleList, blend: false, depthWrite: false, targets: 1,
                            depthTest: false, grid: true);
        _pGround = Pipeline(api, _rpColor, "fs_ground"u8, VkPrimitiveTopology.TriangleList, blend: true, depthWrite: false, targets: 1, grid: true);
        _pBackdrop = Pipeline(api, _rpColor, "fs_backdrop"u8, VkPrimitiveTopology.TriangleList, blend: false, depthWrite: false, targets: 1,
                              depthTest: false, grid: true);
        var sci = new VkSamplerCreateInfo
        {
            magFilter = VkFilter.Linear, minFilter = VkFilter.Linear, mipmapMode = VkSamplerMipmapMode.Linear,
            addressModeU = VkSamplerAddressMode.ClampToEdge, addressModeV = VkSamplerAddressMode.ClampToEdge,
            addressModeW = VkSamplerAddressMode.ClampToEdge, minLod = 0, maxLod = 1000f,
        };
        VkSampler sampler;
        Check(api.vkCreateSampler(&sci, null, &sampler), "vkCreateSampler");
        _sampler = sampler;
        // brief-em3d-107 — the shadow map's comparison sampler: lit where the reference is at or before the stored depth; linear (four
        // comparisons blended a tap) where the device filters D32 linearly, else nearest.
        var filter = _depthLinear ? VkFilter.Linear : VkFilter.Nearest;
        var csi = new VkSamplerCreateInfo
        {
            magFilter = filter, minFilter = filter, mipmapMode = VkSamplerMipmapMode.Nearest,
            addressModeU = VkSamplerAddressMode.ClampToEdge, addressModeV = VkSamplerAddressMode.ClampToEdge,
            addressModeW = VkSamplerAddressMode.ClampToEdge, compareEnable = true, compareOp = VkCompareOp.LessOrEqual, minLod = 0, maxLod = 0,
        };
        VkSampler shadowSampler;
        Check(api.vkCreateSampler(&csi, null, &shadowSampler), "vkCreateSampler");
        _shadowSampler = shadowSampler;

        // two uniform blocks (pick, colour) per frame slot — host-coherent, mapped once
        _ub = NewBuffer(api, Ring * 2 * UniformStride, VkBufferUsageFlags.UniformBuffer, VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent);
        void* um;
        Check(api.vkMapMemory(_ub.Mem, 0, VK_WHOLE_SIZE, 0, &um), "vkMapMemory");
        _uMapped = (byte*)um;
        _tb = NewBuffer(api, Ring * _transformSlots * TransformStride, VkBufferUsageFlags.UniformBuffer, VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent);
        void* tm;
        Check(api.vkMapMemory(_tb.Mem, 0, VK_WHOLE_SIZE, 0, &tm), "vkMapMemory");
        _tMapped = (byte*)tm;
        var ps = stackalloc VkDescriptorPoolSize[2];
        ps[0] = new VkDescriptorPoolSize { type = VkDescriptorType.UniformBufferDynamic, descriptorCount = 2 };
        ps[1] = new VkDescriptorPoolSize { type = VkDescriptorType.UniformBuffer, descriptorCount = 1 };
        var dpi = new VkDescriptorPoolCreateInfo { maxSets = 1, poolSizeCount = 2, pPoolSizes = ps };
        VkDescriptorPool dp;
        Check(api.vkCreateDescriptorPool(&dpi, null, &dp), "vkCreateDescriptorPool");
        _pool = dp;
        var dai = new VkDescriptorSetAllocateInfo { descriptorPool = dp, descriptorSetCount = 1, pSetLayouts = &setLayout };
        VkDescriptorSet set;
        Check(api.vkAllocateDescriptorSets(&dai, &set), "vkAllocateDescriptorSets");
        _set = set;
        var dbi = new VkDescriptorBufferInfo { buffer = _ub.Buf, offset = 0, range = Scene3DFramePlan.UniformBytes };
        var tbi = new VkDescriptorBufferInfo { buffer = _tb.Buf, offset = 0, range = Scene3DFramePlan.TransformBytesPerDraw };
        _apb = NewBuffer(api, Pbr.TableBytes, VkBufferUsageFlags.UniformBuffer, VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent);
        void* am;
        Check(api.vkMapMemory(_apb.Mem, 0, VK_WHOLE_SIZE, 0, &am), "vkMapMemory");
        _apMapped = (byte*)am;
        new Span<byte>(_apMapped, Pbr.TableBytes).Clear();
        var abi = new VkDescriptorBufferInfo { buffer = _apb.Buf, offset = 0, range = Pbr.TableBytes };
        var writes = stackalloc VkWriteDescriptorSet[3];
        writes[0] = new VkWriteDescriptorSet { dstSet = set, dstBinding = 0, descriptorCount = 1, descriptorType = VkDescriptorType.UniformBufferDynamic, pBufferInfo = &dbi };
        writes[1] = new VkWriteDescriptorSet { dstSet = set, dstBinding = 1, descriptorCount = 1, descriptorType = VkDescriptorType.UniformBufferDynamic, pBufferInfo = &tbi };
        writes[2] = new VkWriteDescriptorSet { dstSet = set, dstBinding = 2, descriptorCount = 1, descriptorType = VkDescriptorType.UniformBuffer, pBufferInfo = &abi };
        api.vkUpdateDescriptorSets(3, writes, 0, null);

        var cai = new VkCommandBufferAllocateInfo { commandPool = _cmdPool, level = VkCommandBufferLevel.Primary, commandBufferCount = Ring };
        fixed (VkCommandBuffer* c = _cmd) Check(api.vkAllocateCommandBuffers(&cai, c), "vkAllocateCommandBuffers");
        for (int i = 0; i < Ring; i++)
        {
            var fci = new VkFenceCreateInfo();
            VkFence f;
            Check(api.vkCreateFence(&fci, null, &f), "vkCreateFence");
            _fence[i] = f;
            _pickBuf[i] = NewBuffer(api, 32, VkBufferUsageFlags.TransferDst, VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent);
            void* pm;
            Check(api.vkMapMemory(_pickBuf[i].Mem, 0, VK_WHOLE_SIZE, 0, &pm), "vkMapMemory");
            _pickMapped[i] = (byte*)pm;
        }
        _pickId = NewImage(api, 1, 1, VkFormat.R32G32Uint, VkImageUsageFlags.ColorAttachment | VkImageUsageFlags.TransferSrc, VkImageAspectFlags.Color, export: false);
        _pickPos = NewImage(api, 1, 1, VkFormat.R32G32B32A32Sfloat, VkImageUsageFlags.ColorAttachment | VkImageUsageFlags.TransferSrc, VkImageAspectFlags.Color, export: false);
        _pickDepth = NewImage(api, 1, 1, DepthFormat, VkImageUsageFlags.DepthStencilAttachment, VkImageAspectFlags.Depth, export: false);
        _pickFb = Framebuffer(api, _rpPick, [_pickId.View, _pickPos.View, _pickDepth.View], 1, 1);

        // brief-em3d-107 — set 3, pointed at 1 × 1 stand-ins until a map or the occlusion's targets exist
        // the stand-ins hold what "none" means — a map with nothing in it (depth 1: lit) and no occlusion (1) — rather than undefined
        // contents, in case one is ever read with its uniform switched on
        _noShadowMap = NewImage(api, 1, 1, DepthFormat, VkImageUsageFlags.DepthStencilAttachment | VkImageUsageFlags.Sampled | VkImageUsageFlags.TransferDst,
                                VkImageAspectFlags.Depth, export: false);
        _noOcclusion = NewImage(api, 1, 1, VkFormat.R8Unorm, VkImageUsageFlags.ColorAttachment | VkImageUsageFlags.Sampled | VkImageUsageFlags.TransferDst,
                                VkImageAspectFlags.Color, export: false);
        ClearToShaderRead(api, _noShadowMap, VkImageAspectFlags.Depth);
        ClearToShaderRead(api, _noOcclusion, VkImageAspectFlags.Color);
        var lps = stackalloc VkDescriptorPoolSize[2];
        lps[0] = new VkDescriptorPoolSize { type = VkDescriptorType.SampledImage, descriptorCount = 4 };
        lps[1] = new VkDescriptorPoolSize { type = VkDescriptorType.Sampler, descriptorCount = 1 };
        var lpi = new VkDescriptorPoolCreateInfo { maxSets = 1, poolSizeCount = 2, pPoolSizes = lps };
        VkDescriptorPool lpool;
        Check(api.vkCreateDescriptorPool(&lpi, null, &lpool), "vkCreateDescriptorPool");
        _lightPool = lpool;
        var lai = new VkDescriptorSetAllocateInfo { descriptorPool = lpool, descriptorSetCount = 1, pSetLayouts = &lightLayout };
        VkDescriptorSet lset;
        Check(api.vkAllocateDescriptorSets(&lai, &lset), "vkAllocateDescriptorSets");
        _lightSet = lset;
        WriteLightSet(api);
    }

    /// <summary>brief-em3d-107 — a render pass whose attachments are READ by later passes: an optional colour target of
    /// <paramref name="color"/> (Undefined: none) and an optional depth target, each cleared and left SHADER_READ_ONLY_OPTIMAL — except
    /// the prepass's depth buffer (a colour target with depth), which nothing reads. Its dependencies order the previous frame's reads
    /// before these writes, and these writes before the fragment shader reads that follow.</summary>
    private VkRenderPass ReadBackPass(VkDeviceApi api, VkFormat color, bool depth)
    {
        bool hasColor = color != VkFormat.Undefined;
        var att = stackalloc VkAttachmentDescription[2];
        uint n = 0;
        if (hasColor)
            att[n++] = new VkAttachmentDescription
            {
                format = color, samples = VkSampleCountFlags.Count1,
                loadOp = depth ? VkAttachmentLoadOp.Clear : VkAttachmentLoadOp.DontCare, storeOp = VkAttachmentStoreOp.Store,
                stencilLoadOp = VkAttachmentLoadOp.DontCare, stencilStoreOp = VkAttachmentStoreOp.DontCare,
                initialLayout = VkImageLayout.Undefined, finalLayout = VkImageLayout.ShaderReadOnlyOptimal,
            };
        if (depth)
            att[n++] = new VkAttachmentDescription
            {
                format = DepthFormat, samples = VkSampleCountFlags.Count1, loadOp = VkAttachmentLoadOp.Clear,
                storeOp = hasColor ? VkAttachmentStoreOp.DontCare : VkAttachmentStoreOp.Store,
                stencilLoadOp = VkAttachmentLoadOp.DontCare, stencilStoreOp = VkAttachmentStoreOp.DontCare,
                initialLayout = VkImageLayout.Undefined,
                finalLayout = hasColor ? VkImageLayout.DepthStencilAttachmentOptimal : VkImageLayout.ShaderReadOnlyOptimal,
            };
        var cref = new VkAttachmentReference { attachment = 0, layout = VkImageLayout.ColorAttachmentOptimal };
        var dref = new VkAttachmentReference { attachment = hasColor ? 1u : 0u, layout = VkImageLayout.DepthStencilAttachmentOptimal };
        var sub = new VkSubpassDescription
        {
            pipelineBindPoint = VkPipelineBindPoint.Graphics, colorAttachmentCount = hasColor ? 1u : 0u, pColorAttachments = hasColor ? &cref : null,
            pDepthStencilAttachment = depth ? &dref : null,
        };
        var writes = VkPipelineStageFlags.ColorAttachmentOutput | VkPipelineStageFlags.EarlyFragmentTests | VkPipelineStageFlags.LateFragmentTests;
        var deps = stackalloc VkSubpassDependency[2];
        deps[0] = new VkSubpassDependency
        {
            srcSubpass = VK_SUBPASS_EXTERNAL, dstSubpass = 0,
            srcStageMask = VkPipelineStageFlags.FragmentShader | writes, dstStageMask = writes,
            srcAccessMask = VkAccessFlags.ColorAttachmentWrite | VkAccessFlags.DepthStencilAttachmentWrite,
            dstAccessMask = VkAccessFlags.ColorAttachmentWrite | VkAccessFlags.DepthStencilAttachmentWrite,
        };
        deps[1] = new VkSubpassDependency
        {
            srcSubpass = 0, dstSubpass = VK_SUBPASS_EXTERNAL, srcStageMask = writes, dstStageMask = VkPipelineStageFlags.FragmentShader,
            srcAccessMask = VkAccessFlags.ColorAttachmentWrite | VkAccessFlags.DepthStencilAttachmentWrite, dstAccessMask = VkAccessFlags.ShaderRead,
        };
        var rpi = new VkRenderPassCreateInfo { attachmentCount = n, pAttachments = att, subpassCount = 1, pSubpasses = &sub, dependencyCount = 2, pDependencies = deps };
        VkRenderPass rp;
        Check(api.vkCreateRenderPass(&rpi, null, &rp), "vkCreateRenderPass");
        return rp;
    }

    /// <summary>A new image straight to SHADER_READ_ONLY_OPTIMAL, so set 3 may name it before anything has drawn into it.</summary>
    private void ToShaderRead(VkDeviceApi api, Target t, VkImageAspectFlags aspect)
    {
        var img = t.Image;
        OneShot(api, cb =>
        {
            var b = new VkImageMemoryBarrier
            {
                srcAccessMask = 0, dstAccessMask = VkAccessFlags.ShaderRead,
                oldLayout = VkImageLayout.Undefined, newLayout = VkImageLayout.ShaderReadOnlyOptimal,
                srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED, dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED, image = img,
                subresourceRange = new VkImageSubresourceRange { aspectMask = aspect, levelCount = 1, layerCount = 1 },
            };
            api.vkCmdPipelineBarrier(cb, VkPipelineStageFlags.TopOfPipe, VkPipelineStageFlags.FragmentShader, 0, 0, null, 0, null, 1, &b);
        });
    }

    /// <summary>A new stand-in cleared to 1 (depth 1: nothing in the map; occlusion 1: none) and left SHADER_READ_ONLY_OPTIMAL.</summary>
    private void ClearToShaderRead(VkDeviceApi api, Target t, VkImageAspectFlags aspect)
    {
        var img = t.Image;
        OneShot(api, cb =>
        {
            var range = new VkImageSubresourceRange { aspectMask = aspect, levelCount = 1, layerCount = 1 };
            var b = new VkImageMemoryBarrier
            {
                srcAccessMask = 0, dstAccessMask = VkAccessFlags.TransferWrite,
                oldLayout = VkImageLayout.Undefined, newLayout = VkImageLayout.TransferDstOptimal,
                srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED, dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED, image = img, subresourceRange = range,
            };
            api.vkCmdPipelineBarrier(cb, VkPipelineStageFlags.TopOfPipe, VkPipelineStageFlags.Transfer, 0, 0, null, 0, null, 1, &b);
            if (aspect == VkImageAspectFlags.Depth)
            {
                var d = new VkClearDepthStencilValue(1f, 0u);
                api.vkCmdClearDepthStencilImage(cb, img, VkImageLayout.TransferDstOptimal, &d, 1, &range);
            }
            else
            {
                var c = new VkClearColorValue(1f, 1f, 1f, 1f);
                api.vkCmdClearColorImage(cb, img, VkImageLayout.TransferDstOptimal, &c, 1, &range);
            }
            b.srcAccessMask = VkAccessFlags.TransferWrite; b.dstAccessMask = VkAccessFlags.ShaderRead;
            b.oldLayout = VkImageLayout.TransferDstOptimal; b.newLayout = VkImageLayout.ShaderReadOnlyOptimal;
            api.vkCmdPipelineBarrier(cb, VkPipelineStageFlags.Transfer, VkPipelineStageFlags.FragmentShader, 0, 0, null, 0, null, 1, &b);
        });
    }

    /// <summary>Set 3 pointed at the current shadow map and occlusion targets (or the stand-ins). Never under a frame in flight: the
    /// callers wait for the device first.</summary>
    private void WriteLightSet(VkDeviceApi api)
    {
        var ii = stackalloc VkDescriptorImageInfo[5];
        var ro = VkImageLayout.ShaderReadOnlyOptimal;
        ii[0] = new VkDescriptorImageInfo { imageView = (_shadowMap?.Depth ?? _noShadowMap!).View, imageLayout = ro };
        ii[1] = new VkDescriptorImageInfo { sampler = _shadowSampler };
        ii[2] = new VkDescriptorImageInfo { imageView = (_occlusion?.Blur ?? _noOcclusion!).View, imageLayout = ro };
        ii[3] = new VkDescriptorImageInfo { imageView = (_occlusion?.Depths ?? _noOcclusion!).View, imageLayout = ro };
        ii[4] = new VkDescriptorImageInfo { imageView = (_occlusion?.Raw ?? _noOcclusion!).View, imageLayout = ro };
        var w = stackalloc VkWriteDescriptorSet[5];
        for (uint k = 0; k < 5; k++)
            w[k] = new VkWriteDescriptorSet
            {
                dstSet = _lightSet, dstBinding = k, descriptorCount = 1,
                descriptorType = k == 1 ? VkDescriptorType.Sampler : VkDescriptorType.SampledImage, pImageInfo = &ii[k],
            };
        api.vkUpdateDescriptorSets(5, w, 0, null);
    }

    /// <summary>R-em3d107-1a — the key light's map at <paramref name="size"/>², made (and set 3 re-pointed) only when the size changes.</summary>
    private void EnsureShadowMap(VkDeviceApi api, int size)
    {
        if (_shadowMap?.Size == size) return;
        Check(api.vkDeviceWaitIdle(), "vkDeviceWaitIdle");
        DestroyShadowMap(api);
        // set 3 back on the stand-in first: an allocation that fails below must not leave it naming a destroyed view
        WriteLightSet(api);
        var depth = NewImage(api, size, size, DepthFormat, VkImageUsageFlags.DepthStencilAttachment | VkImageUsageFlags.Sampled,
                             VkImageAspectFlags.Depth, export: false);
        _shadowMap = new ShadowMap { Depth = depth, Size = size, Framebuffer = Framebuffer(api, _rpShadow, [depth.View], size, size) };
        WriteLightSet(api);
    }

    /// <summary>R-em3d107-2a — the occlusion's targets at the frame's size, made (and set 3 re-pointed) only when the size changes.</summary>
    private void EnsureOcclusion(VkDeviceApi api, int w, int h)
    {
        if (_occlusion is { } o && o.Width == w && o.Height == h) return;
        Check(api.vkDeviceWaitIdle(), "vkDeviceWaitIdle");
        DestroyOcclusion(api);
        WriteLightSet(api);
        var sampled = VkImageUsageFlags.ColorAttachment | VkImageUsageFlags.Sampled;
        var n = new OcclusionTargets
        {
            Width = w, Height = h,
            Depths = NewImage(api, w, h, VkFormat.R32Sfloat, sampled, VkImageAspectFlags.Color, export: false),
            DepthBuffer = NewImage(api, w, h, DepthFormat, VkImageUsageFlags.DepthStencilAttachment, VkImageAspectFlags.Depth, export: false),
            Raw = NewImage(api, w, h, VkFormat.R8Unorm, sampled, VkImageAspectFlags.Color, export: false),
            Blur = NewImage(api, w, h, VkFormat.R8Unorm, sampled, VkImageAspectFlags.Color, export: false),
        };
        n.Prepass = Framebuffer(api, _rpPrepass, [n.Depths.View, n.DepthBuffer.View], w, h);
        n.AoPass = Framebuffer(api, _rpAo, [n.Raw.View], w, h);
        n.BlurPass = Framebuffer(api, _rpAo, [n.Blur.View], w, h);
        // the blurred occlusion may be named by set 3 before a frame has drawn it (a frame that turns occlusion off)
        ToShaderRead(api, n.Blur, VkImageAspectFlags.Color);
        _occlusion = n;
        WriteLightSet(api);
    }

    private void DestroyShadowMap(VkDeviceApi api)
    {
        if (_shadowMap is not { } m) return;
        api.vkDestroyFramebuffer(m.Framebuffer, null);
        Destroy(api, m.Depth);
        _shadowMap = null;
    }

    private void DestroyOcclusion(VkDeviceApi api)
    {
        if (_occlusion is not { } o) return;
        api.vkDestroyFramebuffer(o.Prepass, null); api.vkDestroyFramebuffer(o.AoPass, null); api.vkDestroyFramebuffer(o.BlurPass, null);
        Destroy(api, o.Depths); Destroy(api, o.DepthBuffer); Destroy(api, o.Raw); Destroy(api, o.Blur);
        _occlusion = null;
    }

    private VkRenderPass RenderPass(VkDeviceApi api, bool pick)
    {
        int colors = pick ? 2 : 1;
        var att = stackalloc VkAttachmentDescription[3];
        for (int i = 0; i < colors; i++)
            att[i] = new VkAttachmentDescription
            {
                format = pick ? (i == 0 ? VkFormat.R32G32Uint : VkFormat.R32G32B32A32Sfloat) : ColorFormat,
                samples = VkSampleCountFlags.Count1, loadOp = VkAttachmentLoadOp.Clear, storeOp = VkAttachmentStoreOp.Store,
                stencilLoadOp = VkAttachmentLoadOp.DontCare, stencilStoreOp = VkAttachmentStoreOp.DontCare,
                // TRANSFER_SRC_OPTIMAL: what both of Avalonia's importers read from, and what the pick copy reads
                initialLayout = VkImageLayout.Undefined, finalLayout = VkImageLayout.TransferSrcOptimal,
            };
        att[colors] = new VkAttachmentDescription
        {
            format = DepthFormat, samples = VkSampleCountFlags.Count1, loadOp = VkAttachmentLoadOp.Clear, storeOp = VkAttachmentStoreOp.DontCare,
            stencilLoadOp = VkAttachmentLoadOp.DontCare, stencilStoreOp = VkAttachmentStoreOp.DontCare,
            initialLayout = VkImageLayout.Undefined, finalLayout = VkImageLayout.DepthStencilAttachmentOptimal,
        };
        var cref = stackalloc VkAttachmentReference[2];
        cref[0] = new VkAttachmentReference { attachment = 0, layout = VkImageLayout.ColorAttachmentOptimal };
        cref[1] = new VkAttachmentReference { attachment = 1, layout = VkImageLayout.ColorAttachmentOptimal };
        var dref = new VkAttachmentReference { attachment = (uint)colors, layout = VkImageLayout.DepthStencilAttachmentOptimal };
        var sub = new VkSubpassDescription
        {
            pipelineBindPoint = VkPipelineBindPoint.Graphics, colorAttachmentCount = (uint)colors, pColorAttachments = cref,
            pDepthStencilAttachment = &dref,
        };
        var deps = stackalloc VkSubpassDependency[2];
        // in: the previous reader (the compositor's copy, or our own pick copy) is done before we clear
        deps[0] = new VkSubpassDependency
        {
            srcSubpass = VK_SUBPASS_EXTERNAL, dstSubpass = 0,
            srcStageMask = VkPipelineStageFlags.ColorAttachmentOutput | VkPipelineStageFlags.Transfer | VkPipelineStageFlags.LateFragmentTests,
            dstStageMask = VkPipelineStageFlags.ColorAttachmentOutput | VkPipelineStageFlags.EarlyFragmentTests,
            // The previous frame's attachment WRITES, made available before this one's (write after write).
            srcAccessMask = VkAccessFlags.ColorAttachmentWrite | VkAccessFlags.DepthStencilAttachmentWrite,
            dstAccessMask = VkAccessFlags.ColorAttachmentWrite | VkAccessFlags.DepthStencilAttachmentWrite,
        };
        // out: the colour writes are visible to a transfer read (the compositor's, or the pick copy)
        deps[1] = new VkSubpassDependency
        {
            srcSubpass = 0, dstSubpass = VK_SUBPASS_EXTERNAL,
            srcStageMask = VkPipelineStageFlags.ColorAttachmentOutput, dstStageMask = VkPipelineStageFlags.Transfer | VkPipelineStageFlags.BottomOfPipe,
            srcAccessMask = VkAccessFlags.ColorAttachmentWrite, dstAccessMask = VkAccessFlags.TransferRead,
        };
        var rpi = new VkRenderPassCreateInfo
        {
            attachmentCount = (uint)(colors + 1), pAttachments = att, subpassCount = 1, pSubpasses = &sub,
            dependencyCount = 2, pDependencies = deps,
        };
        VkRenderPass rp;
        Check(api.vkCreateRenderPass(&rpi, null, &rp), "vkCreateRenderPass");
        return rp;
    }

    /// <summary>3D editor round 3 / bugs round 9 — the polygon offset of a draw's depth tie (Scene3DFramePlan.DepthBias), set only
    /// when it changes between draws. A clamp other than 0 needs the device's <c>depthBiasClamp</c> (<see cref="_biasClamp"/>).</summary>
    private void SetTie(VkDeviceApi api, VkCommandBuffer cb, Scene3DDepthTie tie)
    {
        var (constant, slope, clamp) = Scene3DFramePlan.DepthBias(tie);
        if (clamp != 0 && !_biasClamp) (slope, clamp) = (0, 0);
        api.vkCmdSetDepthBias(cb, constant, clamp, slope);
    }

    private VkPipeline Pipeline(VkDeviceApi api, VkRenderPass rp, ReadOnlySpan<byte> fragmentEntry, VkPrimitiveTopology topology,
                                bool blend, bool depthWrite, int targets, bool field = false, bool depthTest = true, bool grid = false,
                                bool image = false, bool pbr = false, bool premultiplied = false, ReadOnlySpan<byte> vertexEntry = default,
                                bool fieldLit = false)
    {
        // brief-em3d-107 — vertexEntry names a vertex stage taking vs's input (vs_shadow); targets 0 is a depth-only pass
        fixed (byte* vsName = !vertexEntry.IsEmpty ? vertexEntry : fieldLit ? "vs_field_lit"u8 : field ? "vs_field"u8 : grid ? "vs_grid"u8 : image ? "vs_image"u8 : pbr ? "vs_pbr"u8 : "vs"u8)
        fixed (byte* fsName = fragmentEntry)
        {
            var stages = stackalloc VkPipelineShaderStageCreateInfo[2];
            stages[0] = new VkPipelineShaderStageCreateInfo { stage = VkShaderStageFlags.Vertex, module = _module, pName = vsName };
            stages[1] = new VkPipelineShaderStageCreateInfo { stage = VkShaderStageFlags.Fragment, module = _module, pName = fsName };
            var vbd = new VkVertexInputBindingDescription
            {
                binding = 0, stride = field ? (uint)FieldVertex.Stride : image ? (uint)Scene3DImageVertex.Stride : Scene3DVertex.Stride,
                inputRate = VkVertexInputRate.Vertex,
            };
            var attrs = stackalloc VkVertexInputAttributeDescription[6];
            uint attrCount = fieldLit ? 4u : field ? 3u : image ? 5u : pbr ? 6u : 4u;
            // brief-em3d-106 — the shade stream: vertex binding 1 (scene.wgsl's header), 16 bytes a vertex.
            var bindings = stackalloc VkVertexInputBindingDescription[2];
            bindings[0] = vbd;
            bindings[1] = new VkVertexInputBindingDescription { binding = 1, stride = (uint)Scene3DShadeVertex.Stride, inputRate = VkVertexInputRate.Vertex };
            // brief-em3d-109 — the Lit field's normals take the same binding, 12 bytes a vertex
            if (fieldLit) bindings[1].stride = (uint)FieldShading.Stride;
            if (image)
            {
                // brief-em3d-101 — Scene3DImageVertex: position, texture coordinate, id, face, colour.
                attrs[0] = new VkVertexInputAttributeDescription { location = 0, binding = 0, format = VkFormat.R32G32B32Sfloat, offset = 0 };
                attrs[1] = new VkVertexInputAttributeDescription { location = 1, binding = 0, format = VkFormat.R32G32Sfloat, offset = 12 };
                attrs[2] = new VkVertexInputAttributeDescription { location = 2, binding = 0, format = VkFormat.R32Uint, offset = 20 };
                attrs[3] = new VkVertexInputAttributeDescription { location = 3, binding = 0, format = VkFormat.R32Uint, offset = 24 };
                attrs[4] = new VkVertexInputAttributeDescription { location = 4, binding = 0, format = VkFormat.R8G8B8A8Unorm, offset = 28 };
            }
            else if (field)
            {
                // brief-em3d-29 — FieldVertex: position, the value's real part, its imaginary part.
                for (uint k = 0; k < 3; k++)
                    attrs[k] = new VkVertexInputAttributeDescription { location = k, binding = 0, format = VkFormat.R32G32B32Sfloat, offset = 12 * k };
                if (fieldLit) attrs[3] = new VkVertexInputAttributeDescription { location = 3, binding = 1, format = VkFormat.R32G32B32Sfloat, offset = 0 };
            }
            else
            {
                attrs[0] = new VkVertexInputAttributeDescription { location = 0, binding = 0, format = VkFormat.R32G32B32Sfloat, offset = 0 };
                attrs[1] = new VkVertexInputAttributeDescription { location = 1, binding = 0, format = VkFormat.R32Uint, offset = 12 };
                attrs[2] = new VkVertexInputAttributeDescription { location = 2, binding = 0, format = VkFormat.R8G8B8A8Unorm, offset = 16 };
                attrs[3] = new VkVertexInputAttributeDescription { location = 3, binding = 0, format = VkFormat.R32Uint, offset = 20 };
                if (pbr)
                {
                    attrs[4] = new VkVertexInputAttributeDescription { location = 4, binding = 1, format = VkFormat.R32G32B32Sfloat, offset = 0 };
                    attrs[5] = new VkVertexInputAttributeDescription { location = 5, binding = 1, format = VkFormat.R32Uint, offset = 12 };
                }
            }
            var vin = grid
                ? new VkPipelineVertexInputStateCreateInfo()
                : new VkPipelineVertexInputStateCreateInfo
                {
                    vertexBindingDescriptionCount = pbr || fieldLit ? 2u : 1u, pVertexBindingDescriptions = bindings,
                    vertexAttributeDescriptionCount = attrCount, pVertexAttributeDescriptions = attrs,
                };
            var ia = new VkPipelineInputAssemblyStateCreateInfo { topology = topology };
            var vp = new VkPipelineViewportStateCreateInfo { viewportCount = 1, scissorCount = 1 };
            // cull none; counter-clockwise front faces — see the header on why FlipY is false
            var rs = new VkPipelineRasterizationStateCreateInfo
            {
                polygonMode = VkPolygonMode.Fill, cullMode = VkCullModeFlags.None, frontFace = VkFrontFace.CounterClockwise, lineWidth = 1f,
                // 3D editor round 3 / bugs round 9 — the depth tie's polygon offset is DYNAMIC state (SetTie), on every pipeline so
                // no bind disturbs it: four ties across three triangle pipelines would otherwise be twelve static twins.
                depthBiasEnable = true,
            };
            var ms = new VkPipelineMultisampleStateCreateInfo { rasterizationSamples = VkSampleCountFlags.Count1 };
            var ds = new VkPipelineDepthStencilStateCreateInfo { depthTestEnable = depthTest, depthWriteEnable = depthWrite, depthCompareOp = VkCompareOp.LessOrEqual };
            var cba = stackalloc VkPipelineColorBlendAttachmentState[2];
            for (int i = 0; i < targets; i++)
                cba[i] = new VkPipelineColorBlendAttachmentState
                {
                    blendEnable = blend,
                    // brief-em3d-106 — PbrTranslucent's colour is premultiplied: ONE on RGB
                    srcColorBlendFactor = premultiplied ? VkBlendFactor.One : VkBlendFactor.SrcAlpha, dstColorBlendFactor = VkBlendFactor.OneMinusSrcAlpha,
                    colorBlendOp = VkBlendOp.Add,
                    // the shared image's alpha stays 1 (R-em3d28-1d)
                    srcAlphaBlendFactor = VkBlendFactor.One, dstAlphaBlendFactor = VkBlendFactor.OneMinusSrcAlpha, alphaBlendOp = VkBlendOp.Add,
                    colorWriteMask = VkColorComponentFlags.All,
                };
            var cb = new VkPipelineColorBlendStateCreateInfo { attachmentCount = (uint)targets, pAttachments = cba };
            var dyn = stackalloc VkDynamicState[3] { VkDynamicState.Viewport, VkDynamicState.Scissor, VkDynamicState.DepthBias };
            var dy = new VkPipelineDynamicStateCreateInfo { dynamicStateCount = 3, pDynamicStates = dyn };
            var gpi = new VkGraphicsPipelineCreateInfo
            {
                stageCount = 2, pStages = stages, pVertexInputState = &vin, pInputAssemblyState = &ia, pViewportState = &vp,
                pRasterizationState = &rs, pMultisampleState = &ms, pDepthStencilState = &ds, pColorBlendState = &cb, pDynamicState = &dy,
                layout = _layout, renderPass = rp, subpass = 0,
            };
            VkPipeline p;
            Check(api.vkCreateGraphicsPipelines(VkPipelineCache.Null, 1, &gpi, null, &p), "vkCreateGraphicsPipelines");
            return p;
        }
    }

    private (VkBuffer, VkDeviceMemory) NewBuffer(VkDeviceApi api, long size, VkBufferUsageFlags usage, VkMemoryPropertyFlags props)
    {
        var bci = new VkBufferCreateInfo { size = (ulong)size, usage = usage, sharingMode = VkSharingMode.Exclusive };
        VkBuffer b;
        Check(api.vkCreateBuffer(&bci, null, &b), "vkCreateBuffer");
        VkMemoryRequirements req;
        api.vkGetBufferMemoryRequirements(b, &req);
        var mai = new VkMemoryAllocateInfo { allocationSize = req.size, memoryTypeIndex = MemoryType(req.memoryTypeBits, props) };
        VkDeviceMemory m = default;
        try
        {
            // out of memory here (a large photo's staging buffer) must not leave the buffer behind
            Check(api.vkAllocateMemory(&mai, null, &m), "vkAllocateMemory");
            Check(api.vkBindBufferMemory(b, m, 0), "vkBindBufferMemory");
        }
        catch
        {
            if (m.Handle != 0) api.vkFreeMemory(m, null);
            api.vkDestroyBuffer(b, null);
            throw;
        }
        return (b, m);
    }

    private Target NewImage(VkDeviceApi api, int w, int h, VkFormat fmt, VkImageUsageFlags usage, VkImageAspectFlags aspect, bool export)
    {
        // exactly the parameters Avalonia's importer re-creates the image with (header)
        var emi = new VkExternalMemoryImageCreateInfo { handleTypes = VkExternalMemoryHandleTypeFlags.OpaqueFD };
        var ici = new VkImageCreateInfo
        {
            pNext = export ? &emi : null,
            flags = export ? VkImageCreateFlags.MutableFormat : VkImageCreateFlags.None,
            imageType = VkImageType.Image2D, format = fmt, extent = new VkExtent3D { width = (uint)w, height = (uint)h, depth = 1 },
            mipLevels = 1, arrayLayers = 1, samples = VkSampleCountFlags.Count1,
            tiling = export && _linearShared ? VkImageTiling.Linear : VkImageTiling.Optimal,
            usage = usage, sharingMode = VkSharingMode.Exclusive, initialLayout = VkImageLayout.Undefined,
        };
        var t = new Target();
        VkImage img;
        Check(api.vkCreateImage(&ici, null, &img), "vkCreateImage");
        t.Image = img;
        VkMemoryRequirements req;
        api.vkGetImageMemoryRequirements(img, &req);
        var ded = new VkMemoryDedicatedAllocateInfo { image = img };
        var exp = new VkExportMemoryAllocateInfo { handleTypes = VkExternalMemoryHandleTypeFlags.OpaqueFD, pNext = &ded };
        var mai = new VkMemoryAllocateInfo
        {
            pNext = export ? &exp : null, allocationSize = req.size,
            memoryTypeIndex = MemoryType(req.memoryTypeBits, VkMemoryPropertyFlags.DeviceLocal),
        };
        VkDeviceMemory m;
        Check(api.vkAllocateMemory(&mai, null, &m), "vkAllocateMemory");
        Check(api.vkBindImageMemory(img, m, 0), "vkBindImageMemory");
        t.Memory = m;
        t.MemorySize = req.size;
        var vci = new VkImageViewCreateInfo
        {
            image = img, viewType = VkImageViewType.Image2D, format = fmt,
            subresourceRange = new VkImageSubresourceRange { aspectMask = aspect, levelCount = 1, layerCount = 1 },
        };
        VkImageView v;
        Check(api.vkCreateImageView(&vci, null, &v), "vkCreateImageView");
        t.View = v;
        return t;
    }

    private static void Destroy(VkDeviceApi api, Target? t)
    {
        if (t is null) return;
        api.vkDestroyImageView(t.View, null); api.vkDestroyImage(t.Image, null); api.vkFreeMemory(t.Memory, null);
    }

    private static VkFramebuffer Framebuffer(VkDeviceApi api, VkRenderPass rp, VkImageView[] views, int w, int h)
    {
        fixed (VkImageView* pv = views)
        {
            var fci = new VkFramebufferCreateInfo { renderPass = rp, attachmentCount = (uint)views.Length, pAttachments = pv, width = (uint)w, height = (uint)h, layers = 1 };
            VkFramebuffer fb;
            Check(api.vkCreateFramebuffer(&fci, null, &fb), "vkCreateFramebuffer");
            return fb;
        }
    }

    // ── geometry ────────────────────────────────────────────────────────────────────────────

    public override void UploadScene(Scene3DModel scene)
    {
        var api = Api;
        Free(api, ref _vb); Free(api, ref _ib); Free(api, ref _lines);
        fixed (Scene3DVertex* p = scene.Vertices) _vb = Upload(api, p, scene.Vertices.Length * Scene3DVertex.Stride, VkBufferUsageFlags.VertexBuffer);
        fixed (uint* p = scene.Indices) _ib = Upload(api, p, scene.Indices.Length * 4, VkBufferUsageFlags.IndexBuffer);
        fixed (Scene3DVertex* p = scene.LineVertices) _lines = Upload(api, p, scene.LineVertices.Length * Scene3DVertex.Stride, VkBufferUsageFlags.VertexBuffer);
        Free(api, ref _imageVb);
        fixed (Scene3DImageVertex* p = scene.ImageVertices) _imageVb = Upload(api, p, scene.ImageVertices.Length * Scene3DImageVertex.Stride, VkBufferUsageFlags.VertexBuffer);
        _textures.Sync(scene, UploadTexture, ReleaseTexture);
    }

    /// <summary>
    /// brief-em3d-101 R-em3d101-4d — one image: an RGBA8 UNORM image (never _SRGB) with every CPU-built mip level, staged in ONE
    /// buffer and copied level by level between two layout transitions (undefined → transfer destination → shader read), then a
    /// view over all its levels and a descriptor set from a pool of its own. Once per <see cref="Scene3DTexture"/>.
    /// </summary>
    private Texture? UploadTexture(Scene3DTexture t)
    {
        var levels = t.Levels();
        var tex = NewSampledImage(VkFormat.R8G8B8A8Unorm, [.. levels.Select(l => (l.Width, l.Height, l.Rgba))]);
        if (tex is null) return null;
        var api = Api;
        bool done = false;
        try
        {
            // A pool of exactly one set, owned by this texture and destroyed with it: no shared pool to size, none to exhaust.
            var sizes = stackalloc VkDescriptorPoolSize[2];
            sizes[0] = new VkDescriptorPoolSize { type = VkDescriptorType.SampledImage, descriptorCount = 1 };
            sizes[1] = new VkDescriptorPoolSize { type = VkDescriptorType.Sampler, descriptorCount = 1 };
            var dpi = new VkDescriptorPoolCreateInfo { maxSets = 1, poolSizeCount = 2, pPoolSizes = sizes };
            VkDescriptorPool pool;
            Check(api.vkCreateDescriptorPool(&dpi, null, &pool), "vkCreateDescriptorPool");
            tex.Pool = pool;
            var layout = _imageSetLayout;
            var dai = new VkDescriptorSetAllocateInfo { descriptorPool = pool, descriptorSetCount = 1, pSetLayouts = &layout };
            VkDescriptorSet set;
            Check(api.vkAllocateDescriptorSets(&dai, &set), "vkAllocateDescriptorSets");
            tex.Set = set;
            var ii = new VkDescriptorImageInfo { imageView = tex.View, imageLayout = VkImageLayout.ShaderReadOnlyOptimal };
            var si = new VkDescriptorImageInfo { sampler = _sampler };
            var writes = stackalloc VkWriteDescriptorSet[2];
            writes[0] = new VkWriteDescriptorSet { dstSet = set, dstBinding = 0, descriptorCount = 1, descriptorType = VkDescriptorType.SampledImage, pImageInfo = &ii };
            writes[1] = new VkWriteDescriptorSet { dstSet = set, dstBinding = 1, descriptorCount = 1, descriptorType = VkDescriptorType.Sampler, pImageInfo = &si };
            api.vkUpdateDescriptorSets(2, writes, 0, null);
            done = true;
            return tex;
        }
        finally { if (!done) ReleaseTexture(tex); }
    }

    /// <summary>
    /// brief-em3d-106 R-em3d106-4c — brief 101's upload with the format an argument (RGBA8 for an image, R16G16B16A16_SFLOAT for the
    /// environment, which every Vulkan device samples with linear filtering — a required format — so no RGBE fallback): an image with
    /// every level given, staged in ONE buffer and copied level by level between two layout transitions (undefined → transfer
    /// destination → shader read), and a view over all its levels. No descriptor set: the caller makes the one it binds.
    /// </summary>
    private Texture? NewSampledImage(VkFormat format, IReadOnlyList<(int Width, int Height, byte[] Bytes)> levels)
    {
        var api = Api;
        long total = levels.Sum(l => (long)l.Bytes.Length);
        Counters.CountUpload(total);
        var staging = NewBuffer(api, total, VkBufferUsageFlags.TransferSrc, VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent);
        // A failure part-way (out of device memory on a large photo, above all) releases what was made before it — the staging
        // buffer always, the image, its memory, view and pool when the upload does not complete — and is then thrown as before.
        var tex = new Texture();
        bool done = false;
        try
        {
            void* p;
            Check(api.vkMapMemory(staging.Item2, 0, (ulong)total, 0, &p), "vkMapMemory");
            long at = 0;
            foreach (var l in levels) { l.Bytes.AsSpan().CopyTo(new Span<byte>((byte*)p + at, l.Bytes.Length)); at += l.Bytes.Length; }
            api.vkUnmapMemory(staging.Item2);

            uint mips = (uint)levels.Count;
            var ici = new VkImageCreateInfo
            {
                imageType = VkImageType.Image2D, format = format,
                extent = new VkExtent3D { width = (uint)levels[0].Width, height = (uint)levels[0].Height, depth = 1 },
                mipLevels = mips, arrayLayers = 1, samples = VkSampleCountFlags.Count1, tiling = VkImageTiling.Optimal,
                usage = VkImageUsageFlags.Sampled | VkImageUsageFlags.TransferDst, sharingMode = VkSharingMode.Exclusive,
                initialLayout = VkImageLayout.Undefined,
            };
            VkImage created;
            Check(api.vkCreateImage(&ici, null, &created), "vkCreateImage");
            var img = created;
            tex.Image = img;
            VkMemoryRequirements req;
            api.vkGetImageMemoryRequirements(img, &req);
            var mai = new VkMemoryAllocateInfo { allocationSize = req.size, memoryTypeIndex = MemoryType(req.memoryTypeBits, VkMemoryPropertyFlags.DeviceLocal) };
            VkDeviceMemory mem;
            Check(api.vkAllocateMemory(&mai, null, &mem), "vkAllocateMemory");
            tex.Memory = mem;
            Check(api.vkBindImageMemory(img, mem, 0), "vkBindImageMemory");
            var all = new VkImageSubresourceRange { aspectMask = VkImageAspectFlags.Color, levelCount = mips, layerCount = 1 };
            OneShot(api, cb =>
            {
                var toDst = new VkImageMemoryBarrier
                {
                    srcAccessMask = 0, dstAccessMask = VkAccessFlags.TransferWrite,
                    oldLayout = VkImageLayout.Undefined, newLayout = VkImageLayout.TransferDstOptimal,
                    srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED, dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED, image = img, subresourceRange = all,
                };
                api.vkCmdPipelineBarrier(cb, VkPipelineStageFlags.TopOfPipe, VkPipelineStageFlags.Transfer, 0, 0, null, 0, null, 1, &toDst);
                var regions = new VkBufferImageCopy[levels.Count];
                ulong offset = 0;
                for (int k = 0; k < levels.Count; k++)
                {
                    regions[k] = new VkBufferImageCopy
                    {
                        bufferOffset = offset,
                        imageSubresource = new VkImageSubresourceLayers { aspectMask = VkImageAspectFlags.Color, mipLevel = (uint)k, layerCount = 1 },
                        imageExtent = new VkExtent3D { width = (uint)levels[k].Width, height = (uint)levels[k].Height, depth = 1 },
                    };
                    offset += (ulong)levels[k].Bytes.Length;
                }
                fixed (VkBufferImageCopy* r = regions)
                    api.vkCmdCopyBufferToImage(cb, staging.Item1, img, VkImageLayout.TransferDstOptimal, (uint)regions.Length, r);
                var toRead = new VkImageMemoryBarrier
                {
                    srcAccessMask = VkAccessFlags.TransferWrite, dstAccessMask = VkAccessFlags.ShaderRead,
                    oldLayout = VkImageLayout.TransferDstOptimal, newLayout = VkImageLayout.ShaderReadOnlyOptimal,
                    srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED, dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED, image = img, subresourceRange = all,
                };
                api.vkCmdPipelineBarrier(cb, VkPipelineStageFlags.Transfer, VkPipelineStageFlags.FragmentShader, 0, 0, null, 0, null, 1, &toRead);
            });
            var vci = new VkImageViewCreateInfo { image = img, viewType = VkImageViewType.Image2D, format = format, subresourceRange = all };
            VkImageView view;
            Check(api.vkCreateImageView(&vci, null, &view), "vkCreateImageView");
            tex.View = view;

            done = true;
            return tex;
        }
        finally
        {
            api.vkDestroyBuffer(staging.Item1, null);
            api.vkFreeMemory(staging.Item2, null);
            if (!done) ReleaseTexture(tex);
        }
    }

    /// <summary>A texture no scene draws any more. A frame in flight may still sample it, so the GPU is waited for first — once
    /// per scene change that drops an image, never per frame. Destroying its pool frees its set.</summary>
    private void ReleaseTexture(Texture? t)
    {
        if (t is null || _api is not { } api) return;
        api.vkDeviceWaitIdle();
        if (t.Pool.Handle != 0) api.vkDestroyDescriptorPool(t.Pool, null);
        if (t.View.Handle != 0) api.vkDestroyImageView(t.View, null);
        if (t.Image.Handle != 0) api.vkDestroyImage(t.Image, null);
        if (t.Memory.Handle != 0) api.vkFreeMemory(t.Memory, null);
    }

    // ── the realistic view's lighting (brief-em3d-106) ─────────────────────────────────────────────────────────────────

    /// <summary>The table into the mapped buffer set 0 binding 2 names. A frame in flight may still read it, so the GPU is waited for
    /// first — once per scene whose looks changed, never per frame.</summary>
    public override void UploadAppearances(float[] table)
    {
        var api = Api;
        api.vkDeviceWaitIdle();
        fixed (float* p = table) Buffer.MemoryCopy(p, _apMapped, Pbr.TableBytes, Math.Min(Pbr.TableBytes, table.Length * 4));
        Counters.CountUpload(table.Length * 4L);
        _appearancesWritten = true;
    }

    public override void UploadEnvironment(Render.Scene3D.Look.PrefilteredEnvironment environment)
    {
        var api = Api;
        ReleaseEnvironmentTextures();
        _envMap = NewSampledImage(VkFormat.R16G16B16A16Sfloat, [.. environment.Levels.Select(l => (l.Width, l.Height, l.Bytes.ToArray()))]);
        _envBrdf = NewSampledImage(VkFormat.R16G16B16A16Sfloat,
            [(Pbr.BrdfSize, Pbr.BrdfSize, MemoryMarshal.AsBytes(environment.BrdfTable.AsSpan()).ToArray())]);
        if (_envMap is null || _envBrdf is null) return;
        var sizes = stackalloc VkDescriptorPoolSize[2];
        sizes[0] = new VkDescriptorPoolSize { type = VkDescriptorType.SampledImage, descriptorCount = 2 };
        sizes[1] = new VkDescriptorPoolSize { type = VkDescriptorType.Sampler, descriptorCount = 1 };
        var dpi = new VkDescriptorPoolCreateInfo { maxSets = 1, poolSizeCount = 2, pPoolSizes = sizes };
        VkDescriptorPool pool;
        Check(api.vkCreateDescriptorPool(&dpi, null, &pool), "vkCreateDescriptorPool");
        _envPool = pool;
        var layout = _envSetLayout;
        var dai = new VkDescriptorSetAllocateInfo { descriptorPool = pool, descriptorSetCount = 1, pSetLayouts = &layout };
        VkDescriptorSet set;
        Check(api.vkAllocateDescriptorSets(&dai, &set), "vkAllocateDescriptorSets");
        _envSet = set;
        var mi = new VkDescriptorImageInfo { imageView = _envMap.View, imageLayout = VkImageLayout.ShaderReadOnlyOptimal };
        var si = new VkDescriptorImageInfo { sampler = _sampler };
        var bi = new VkDescriptorImageInfo { imageView = _envBrdf.View, imageLayout = VkImageLayout.ShaderReadOnlyOptimal };
        var writes = stackalloc VkWriteDescriptorSet[3];
        writes[0] = new VkWriteDescriptorSet { dstSet = set, dstBinding = 0, descriptorCount = 1, descriptorType = VkDescriptorType.SampledImage, pImageInfo = &mi };
        writes[1] = new VkWriteDescriptorSet { dstSet = set, dstBinding = 1, descriptorCount = 1, descriptorType = VkDescriptorType.Sampler, pImageInfo = &si };
        writes[2] = new VkWriteDescriptorSet { dstSet = set, dstBinding = 2, descriptorCount = 1, descriptorType = VkDescriptorType.SampledImage, pImageInfo = &bi };
        api.vkUpdateDescriptorSets(3, writes, 0, null);
    }

    /// <summary>The environment's textures and set. The appearance buffer stays (12 KB, made with the device): set 0 names it.</summary>
    public override void ReleaseEnvironment()
    {
        ReleaseEnvironmentTextures();
        _appearancesWritten = false;
        // brief-em3d-107 — the shadow map and the occlusion's targets go too; set 3 falls back to the stand-ins
        if (_api is { } api && (_shadowMap is not null || _occlusion is not null))
        {
            api.vkDeviceWaitIdle();
            DestroyShadowMap(api);
            DestroyOcclusion(api);
            WriteLightSet(api);
        }
    }

    private void ReleaseEnvironmentTextures()
    {
        if (_api is not { } api) return;
        api.vkDeviceWaitIdle();
        if (_envPool.Handle != 0) api.vkDestroyDescriptorPool(_envPool, null);
        (_envPool, _envSet) = (default, default);
        ReleaseTexture(_envMap); ReleaseTexture(_envBrdf);
        (_envMap, _envBrdf) = (null, null);
    }

    /// <summary>brief-em3d-104 R-em3d104-3a — the whole shade stream, device-local, as the vertices are.</summary>
    public override void UploadShade(Scene3DModel scene)
    {
        var api = Api;
        Free(api, ref _shade);
        fixed (Scene3DShadeVertex* p = scene.ShadeVertices)
            _shade = Upload(api, p, scene.ShadeVertices.Length * Scene3DShadeVertex.Stride, VkBufferUsageFlags.VertexBuffer);
    }

    public override void PatchShade(Scene3DModel scene, Scene3DPatch patch)
    {
        if (patch.ShadeRanges.Count == 0) return;
        if (_shade.Buf.Handle == 0) { UploadShade(scene); return; }
        var api = Api;
        api.vkDeviceWaitIdle();
        foreach (var r in patch.ShadeRanges) CopyRange(api, scene, r, _shade.Buf);
    }

    public override void ReleaseShade()
    {
        if (_api is { } api) Free(api, ref _shade);
    }

    /// <summary>brief-em3d-43 gate 6 — the changed ranges only, staged and copied into the buffers in place.
    /// A frame in flight may still read them, so the GPU is waited for first, as a whole upload does.</summary>
    public override void PatchScene(Scene3DModel scene, Scene3DPatch patch)
    {
        var api = Api;
        api.vkDeviceWaitIdle();
        foreach (var r in patch.Ranges)
        {
            var target = r.Buffer switch { Scene3DPatchBuffer.Vertices => _vb, Scene3DPatchBuffer.Indices => _ib, _ => _lines };
            if (target.Buf.Handle == 0) { UploadScene(scene); return; }
            CopyRange(api, scene, r, target.Buf);
        }
    }

    /// <summary>One range of <paramref name="scene"/>'s bytes into <paramref name="dst"/> in place, through a staging buffer. Counted.</summary>
    private void CopyRange(VkDeviceApi api, Scene3DModel scene, Scene3DPatchRange r, VkBuffer dst)
    {
        Counters.CountUpload(r.ByteLength);
        var staging = NewBuffer(api, r.ByteLength, VkBufferUsageFlags.TransferSrc, VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent);
        void* p;
        Check(api.vkMapMemory(staging.Item2, 0, (ulong)r.ByteLength, 0, &p), "vkMapMemory");
        Scene3DPatch.Source(scene, r).CopyTo(new Span<byte>(p, r.ByteLength));
        api.vkUnmapMemory(staging.Item2);
        ulong offset = (ulong)r.ByteOffset, size = (ulong)r.ByteLength;
        OneShot(api, cb =>
        {
            var region = new VkBufferCopy { dstOffset = offset, size = size };
            api.vkCmdCopyBuffer(cb, staging.Item1, dst, 1, &region);
        });
        api.vkDestroyBuffer(staging.Item1, null);
        api.vkFreeMemory(staging.Item2, null);
    }

    public override void UploadOverlay(Scene3DBuffer slot, Scene3DVertex[] lines)
    {
        var api = Api;
        int i = slot - Scene3DBuffer.Overlay0;
        Free(api, ref _overlays[i]);
        fixed (Scene3DVertex* p = lines) _overlays[i] = Upload(api, p, lines.Length * Scene3DVertex.Stride, VkBufferUsageFlags.VertexBuffer);
    }

    public override void UploadField(FieldVertex[] vertices)
    {
        var api = Api;
        Free(api, ref _field);
        fixed (FieldVertex* p = vertices) _field = Upload(api, p, vertices.Length * FieldVertex.Stride, VkBufferUsageFlags.VertexBuffer);
        _fieldCount = vertices.Length;
    }

    public override void UploadFieldNormals(float[] normals)
    {
        var api = Api;
        Free(api, ref _fieldNormals);
        fixed (float* p = normals) _fieldNormals = Upload(api, p, normals.Length * 4, VkBufferUsageFlags.VertexBuffer);
        _fieldNormalCount = normals.Length / FieldShading.Floats;
    }

    /// <summary>A buffer may still be read by a frame in flight, so replacing one waits for the GPU —
    /// once per generation, never per frame.</summary>
    private void Free(VkDeviceApi api, ref (VkBuffer Buf, VkDeviceMemory Mem) b)
    {
        if (b.Buf.Handle == 0) return;
        api.vkDeviceWaitIdle();
        api.vkDestroyBuffer(b.Buf, null);
        api.vkFreeMemory(b.Mem, null);
        b = default;
    }

    /// <summary>Device-local geometry through a staging buffer; every byte counted once.</summary>
    private (VkBuffer, VkDeviceMemory) Upload(VkDeviceApi api, void* data, int length, VkBufferUsageFlags usage)
    {
        if (length == 0) return default;
        Counters.CountUpload(length);
        var staging = NewBuffer(api, length, VkBufferUsageFlags.TransferSrc, VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent);
        void* p;
        Check(api.vkMapMemory(staging.Item2, 0, (ulong)length, 0, &p), "vkMapMemory");
        Buffer.MemoryCopy(data, p, length, length);
        api.vkUnmapMemory(staging.Item2);
        var dst = NewBuffer(api, length, usage | VkBufferUsageFlags.TransferDst, VkMemoryPropertyFlags.DeviceLocal);
        OneShot(api, cb =>
        {
            var region = new VkBufferCopy { size = (ulong)length };
            api.vkCmdCopyBuffer(cb, staging.Item1, dst.Item1, 1, &region);
        });
        api.vkDestroyBuffer(staging.Item1, null);
        api.vkFreeMemory(staging.Item2, null);
        return dst;
    }

    private void OneShot(VkDeviceApi api, Action<VkCommandBuffer> record)
    {
        var ai = new VkCommandBufferAllocateInfo { commandPool = _cmdPool, level = VkCommandBufferLevel.Primary, commandBufferCount = 1 };
        VkCommandBuffer cb;
        Check(api.vkAllocateCommandBuffers(&ai, &cb), "vkAllocateCommandBuffers");
        try
        {
            var bi = new VkCommandBufferBeginInfo { flags = VkCommandBufferUsageFlags.OneTimeSubmit };
            Check(api.vkBeginCommandBuffer(cb, &bi), "vkBeginCommandBuffer");
            record(cb);
            Check(api.vkEndCommandBuffer(cb), "vkEndCommandBuffer");
            var si = new VkSubmitInfo { commandBufferCount = 1, pCommandBuffers = &cb };
            Check(api.vkQueueSubmit(_queue, 1, &si, VkFence.Null), "vkQueueSubmit");
            Check(api.vkQueueWaitIdle(_queue), "vkQueueWaitIdle");
        }
        finally { api.vkFreeCommandBuffers(_cmdPool, 1, &cb); }
    }

    /// <summary>brief-em3d-48 — room for <paramref name="slots"/> transforms per frame slot: when the plan outgrows the
    /// ring (an array's elements), the device is idled, the ring re-made at least twice as large, and binding 1 pointed at
    /// it. Nothing is in flight then, so no descriptor is rewritten under a command buffer.</summary>
    private void EnsureTransformRing(VkDeviceApi api, int slots)
    {
        if (slots <= _transformSlots) return;
        Check(api.vkDeviceWaitIdle(), "vkDeviceWaitIdle");
        api.vkUnmapMemory(_tb.Mem);
        Free(api, _tb);
        _transformSlots = Math.Max(slots, 2 * _transformSlots);
        _tb = NewBuffer(api, Ring * _transformSlots * TransformStride, VkBufferUsageFlags.UniformBuffer,
                        VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent);
        void* tm;
        Check(api.vkMapMemory(_tb.Mem, 0, VK_WHOLE_SIZE, 0, &tm), "vkMapMemory");
        _tMapped = (byte*)tm;
        var tbi = new VkDescriptorBufferInfo { buffer = _tb.Buf, offset = 0, range = Scene3DFramePlan.TransformBytesPerDraw };
        var write = new VkWriteDescriptorSet { dstSet = _set, dstBinding = 1, descriptorCount = 1, descriptorType = VkDescriptorType.UniformBufferDynamic, pBufferInfo = &tbi };
        api.vkUpdateDescriptorSets(1, &write, 0, null);
    }

    private static void Free(VkDeviceApi api, (VkBuffer Buf, VkDeviceMemory Mem) b)
    {
        if (b.Buf.Handle == 0) return;
        api.vkDestroyBuffer(b.Buf, null);
        api.vkFreeMemory(b.Mem, null);
    }

    // ── presentation ────────────────────────────────────────────────────────────────────────

    public override string? CheckInterop(ICompositionGpuInterop interop)
    {
        string images = string.Join(", ", interop.SupportedImageHandleTypes), sems = string.Join(", ", interop.SupportedSemaphoreTypes);
        if (!interop.SupportedImageHandleTypes.Contains(FdHandle) || !interop.SupportedSemaphoreTypes.Contains(FdSemaphore))
            return $"the compositor cannot import a Vulkan image and semaphores by POSIX file descriptor (it offered images [{images}], " +
                   $"semaphores [{sems}]; under GLX that needs the GL driver's GL_EXT_memory_object_fd and GL_EXT_semaphore_fd)";
        var caps = interop.GetSynchronizationCapabilities(FdHandle);
        if (!caps.HasFlag(CompositionGpuImportedImageSynchronizationCapabilities.Semaphores))
            return $"the compositor's Vulkan-image synchronisation [{caps}] offers no semaphores";
        var uuid = interop.DeviceUuid;
        if (_api is null) CreateDevice(uuid);
        else if (uuid is { Length: 16 } && !uuid.AsSpan().SequenceEqual(_deviceUuid))
            return $"the compositor moved to GPU {Convert.ToHexString(uuid)} after the 3D view's device was made on another";
        if (!_canExport)
            return $"{_description} cannot export memory and semaphores as file descriptors (VK_KHR_external_memory_fd / VK_KHR_external_semaphore_fd)";
        return null;
    }

    public override void CreateImages(ICompositionGpuInterop interop, int width, int height, int count)
    {
        ReleaseImages();
        var api = Api;
        _images = new Image[count];
        for (int i = 0; i < count; i++)
        {
            var im = NewSwapImage(api, width, height, export: true);
            var gi = new VkMemoryGetFdInfoKHR { memory = im.Color.Memory, handleType = VkExternalMemoryHandleTypeFlags.OpaqueFD };
            int fd;
            Check(api.vkGetMemoryFdKHR(&gi, &fd), "vkGetMemoryFdKHR");
            if (fd < 0) throw new Viewer3DPresentFault("vkGetMemoryFdKHR returned no file descriptor for the 3D view's image.");
            im.Imported = interop.ImportImage(new PlatformHandle(fd, FdHandle), new PlatformGraphicsExternalImageProperties
            {
                Width = width, Height = height, Format = PlatformGraphicsExternalImageFormat.R8G8B8A8UNorm,
                MemorySize = im.Color.MemorySize, MemoryOffset = 0, TopLeftOrigin = true,
            });
            im.Ready = NewExportableSemaphore(api);
            im.Released = NewExportableSemaphore(api);
            im.ReadyImported = interop.ImportSemaphore(new PlatformHandle(SemaphoreFd(api, im.Ready), FdSemaphore));
            im.ReleasedImported = interop.ImportSemaphore(new PlatformHandle(SemaphoreFd(api, im.Released), FdSemaphore));
            // designer feedback round 34: a lost context fails HERE first, and the faults were never observed.
            ObservePresent(im.Imported.ImportCompleted);
            ObservePresent(im.ReadyImported.ImportCompleted);
            ObservePresent(im.ReleasedImported.ImportCompleted);
            _images[i] = im;
        }
    }

    /// <summary>The headless path: <paramref name="count"/> images no compositor imports, rendered and
    /// read back with <see cref="ReadRgba"/>. What a later headless <c>render</c> of a 3D view, and the
    /// backend's own harness, draw into.</summary>
    internal void CreateOffscreenImages(int width, int height, int count)
    {
        ReleaseImages();
        var api = Api;
        _images = new Image[count];
        for (int i = 0; i < count; i++) _images[i] = NewSwapImage(api, width, height, export: false);
    }

    private Image NewSwapImage(VkDeviceApi api, int w, int h, bool export)
    {
        var im = new Image
        {
            Width = w, Height = h,
            Color = NewImage(api, w, h, ColorFormat, TargetUsage, VkImageAspectFlags.Color, export),
            Depth = NewImage(api, w, h, DepthFormat, VkImageUsageFlags.DepthStencilAttachment, VkImageAspectFlags.Depth, export: false),
        };
        im.Framebuffer = Framebuffer(api, _rpColor, [im.Color.View, im.Depth.View], w, h);
        return im;
    }

    private static VkSemaphore NewExportableSemaphore(VkDeviceApi api)
    {
        var esi = new VkExportSemaphoreCreateInfo { handleTypes = VkExternalSemaphoreHandleTypeFlags.OpaqueFD };
        var sci = new VkSemaphoreCreateInfo { pNext = &esi };
        VkSemaphore s;
        Check(api.vkCreateSemaphore(&sci, null, &s), "vkCreateSemaphore");
        return s;
    }

    private static int SemaphoreFd(VkDeviceApi api, VkSemaphore s)
    {
        var gi = new VkSemaphoreGetFdInfoKHR { semaphore = s, handleType = VkExternalSemaphoreHandleTypeFlags.OpaqueFD };
        int fd;
        Check(api.vkGetSemaphoreFdKHR(&gi, &fd), "vkGetSemaphoreFdKHR");
        if (fd < 0) throw new Viewer3DPresentFault("vkGetSemaphoreFdKHR returned no file descriptor.");
        return fd;
    }

    public override void ReleaseImages()
    {
        if (_images.Length == 0 || _api is not { } api) { _images = []; return; }
        api.vkDeviceWaitIdle();
        // No wait on the compositor's pending update: this runs on the UI thread, and that update
        // completes only after the UI thread commits its batch — a wait here always timed out, stalling
        // every resize. Our side is idle (above), and the compositor's imports hold their own
        // references to the memory and semaphore payloads.
        foreach (var im in _images)
        {
            Dispose(im.Imported); Dispose(im.ReadyImported); Dispose(im.ReleasedImported);
            api.vkDestroyFramebuffer(im.Framebuffer, null);
            Destroy(api, im.Color); Destroy(api, im.Depth);
            if (im.Ready.Handle != 0) api.vkDestroySemaphore(im.Ready, null);
            if (im.Released.Handle != 0) api.vkDestroySemaphore(im.Released, null);
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
        return !im.ReleaseOwed || im.Pending is not { IsCompleted: false } t || t.Wait(timeoutMs);
    }

    public override void Present(CompositionDrawingSurface surface, int image, ulong frame)
    {
        var im = _images[image];
        if (im.Imported is null) throw new Viewer3DPresentFault("The 3D view's image was never imported by the compositor.");
        im.Pending = ObservePresent(surface.UpdateWithSemaphoresAsync(im.Imported, im.ReadyImported!, im.ReleasedImported!));
        im.ReleaseOwed = true;
        im.ReadySignalled = false;
    }

    // ── the frame ───────────────────────────────────────────────────────────────────────────

    public override void Render(int image, Scene3DFramePlan plan, ulong frame) => RenderTo(_images[image], plan, image);

    /// <summary>brief-em3d-29 R-em3d29-5 — the plan drawn into an image of its own (no semaphores, never
    /// imported) and read back: the same pipelines and buffers as the view, the swapchain untouched.</summary>
    public override byte[] RenderPixels(Scene3DFramePlan plan)
    {
        var api = Api;
        var im = NewSwapImage(api, plan.Width, plan.Height, export: false);
        try
        {
            RenderTo(im, plan, -1);
            Check(api.vkDeviceWaitIdle(), "vkDeviceWaitIdle");
            return ReadRgba(im);
        }
        finally
        {
            api.vkDeviceWaitIdle();
            api.vkDestroyFramebuffer(im.Framebuffer, null);
            Destroy(api, im.Color); Destroy(api, im.Depth);
        }
    }

    private void RenderTo(Image im, Scene3DFramePlan plan, int image)
    {
        var api = Api;
        VkSemaphore wait = default, signal = default;
        if (im.ReleaseOwed)
        {
            // a binary semaphore may be waited only once its signal is submitted: the update must be done
            if (im.Pending is { IsCompleted: false })
                throw new Viewer3DPresentFault($"The compositor has not finished with the 3D view's image {image}, so its release cannot be waited for.");
            if (im.Pending is { IsFaulted: true } f)
                throw new Viewer3DPresentFault($"The compositor's update of the 3D view's image {image} failed: {f.Exception?.GetBaseException().Message}");
            // Cancelled: the compositor never took the image, so the release semaphore will never be
            // signalled and waiting on it would stall the GPU queue for good.
            if (im.Pending is { IsCanceled: true })
                throw new Viewer3DPresentFault($"The compositor cancelled its update of the 3D view's image {image}.");
            wait = im.Released;
            im.ReleaseOwed = false;
        }
        if (im.Ready.Handle != 0 && !im.ReadySignalled) signal = im.Ready;

        // brief-em3d-107 — the shadow map and the occlusion's targets are made (and set 3 re-pointed) BEFORE recording: a set may not be
        // rewritten under a command buffer that binds it
        bool shadowPass = plan.Realistic && plan.ShadowPass && plan.ShadowDrawCount > 0 && _vb.Buf.Handle != 0 && _ib.Buf.Handle != 0;
        bool occlusion = plan.Realistic && plan.Occlusion && _vb.Buf.Handle != 0 && _ib.Buf.Handle != 0;
        if (shadowPass) EnsureShadowMap(api, plan.ShadowSize);
        if (occlusion) EnsureOcclusion(api, im.Width, im.Height);
        int f0 = (int)(_frame % Ring);
        CollectPicks(api);
        if (_submitted[f0])
        {
            var fence = _fence[f0];
            Check(api.vkWaitForFences(1, &fence, true, FenceTimeoutNs), "vkWaitForFences");
            ReadPick(f0);
            Check(api.vkResetFences(1, &fence), "vkResetFences");
            _submitted[f0] = false;
        }
        var cb = _cmd[f0];
        Check(api.vkResetCommandBuffer(cb, 0), "vkResetCommandBuffer");
        var bi = new VkCommandBufferBeginInfo { flags = VkCommandBufferUsageFlags.OneTimeSubmit };
        Check(api.vkBeginCommandBuffer(cb, &bi), "vkBeginCommandBuffer");
        int draws = 0;
        var clears = stackalloc VkClearValue[3];
        ulong zero = 0;
        var set = _set;
        // brief-em3d-46 — this frame slot's transforms: slot 0 the identity, then (brief 48) each array element's and its
        // box's, then a preview's copies.
        EnsureTransformRing(api, plan.TransformCount);
        uint tBase = (uint)(f0 * _transformSlots * TransformStride);
        int tCount = Math.Min(plan.TransformCount, _transformSlots);
        fixed (float* xf = plan.Transforms)
            for (int k = 0; k < tCount; k++)
                Buffer.MemoryCopy(xf + Scene3DFramePlan.TransformFloats * k, _tMapped + tBase + k * TransformStride, TransformStride,
                                  Scene3DFramePlan.TransformBytesPerDraw);
        Counters.CountUniform((long)tCount * Scene3DFramePlan.TransformBytesPerDraw);
        var offs = stackalloc uint[2];

        // the frame's uniforms, written once: the shadow and occlusion passes read them, and so does the colour pass
        uint colourOff = (uint)((f0 * 2 + 1) * UniformStride);
        fixed (float* u = plan.Uniforms) Buffer.MemoryCopy(u, _uMapped + colourOff, Scene3DFramePlan.UniformBytes, Scene3DFramePlan.UniformBytes);
        Counters.CountUniform(Scene3DFramePlan.UniformBytes);
        if (shadowPass) draws += ShadowPass(api, cb, plan, set, colourOff, tBase, tCount);
        if (occlusion) draws += OcclusionPasses(api, cb, plan, set, colourOff, tBase, tCount);

        if (plan.Pick && plan.PickDrawCount > 0 && _vb.Buf.Handle != 0 && _ib.Buf.Handle != 0 && !_pickPending[f0])
        {
            uint off = (uint)(f0 * 2 * UniformStride);
            fixed (float* pu = plan.PickUniforms) Buffer.MemoryCopy(pu, _uMapped + off, Scene3DFramePlan.UniformBytes, Scene3DFramePlan.UniformBytes);
            Counters.CountUniform(Scene3DFramePlan.UniformBytes);
            clears[0] = new VkClearValue(new VkClearColorValue(0u, 0u, 0u, 0u));
            clears[1] = new VkClearValue(0f, 0f, 0f, 0f);
            clears[2] = new VkClearValue(1f, 0u);
            var rbi = new VkRenderPassBeginInfo { renderPass = _rpPick, framebuffer = _pickFb, renderArea = new VkRect2D(0, 0, 1, 1), clearValueCount = 3, pClearValues = clears };
            api.vkCmdBeginRenderPass(cb, &rbi, VkSubpassContents.Inline);
            SetViewport(api, cb, 1, 1);
            api.vkCmdBindPipeline(cb, VkPipelineBindPoint.Graphics, _pPick);
            offs[0] = off; offs[1] = tBase;
            api.vkCmdBindDescriptorSets(cb, VkPipelineBindPoint.Graphics, _layout, 0, 1, &set, 2, offs);
            var vb = _vb.Buf;
            api.vkCmdBindVertexBuffers(cb, 0, 1, &vb, &zero);
            api.vkCmdBindIndexBuffer(cb, _ib.Buf, 0, VkIndexType.Uint32);
            int pickTransform = 0;
            var pickTie = Scene3DDepthTie.None;
            SetTie(api, cb, pickTie);
            for (int i = 0; i < plan.PickDrawCount; i++)
            {
                ref var d = ref plan.PickDraws[i];
                if (d.Tie != pickTie) SetTie(api, cb, pickTie = d.Tie);
                // brief-em3d-48 — an array element's pick draw: its translation and id offset.
                if (d.Transform != pickTransform && d.Transform < tCount)
                {
                    pickTransform = d.Transform;
                    offs[1] = tBase + (uint)(pickTransform * TransformStride);
                    api.vkCmdBindDescriptorSets(cb, VkPipelineBindPoint.Graphics, _layout, 0, 1, &set, 2, offs);
                }
                api.vkCmdDrawIndexed(cb, (uint)d.Count, 1, (uint)d.First, 0, 0); draws++;
            }
            api.vkCmdEndRenderPass(cb);
            CopyTexel(api, cb, _pickId.Image, _pickBuf[f0].Buf, 0);
            CopyTexel(api, cb, _pickPos.Image, _pickBuf[f0].Buf, 16);
            var hb = new VkBufferMemoryBarrier
            {
                srcAccessMask = VkAccessFlags.TransferWrite, dstAccessMask = VkAccessFlags.HostRead,
                srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED, dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
                buffer = _pickBuf[f0].Buf, offset = 0, size = VK_WHOLE_SIZE,
            };
            api.vkCmdPipelineBarrier(cb, VkPipelineStageFlags.Transfer, VkPipelineStageFlags.Host, 0, 0, null, 1, &hb, 0, null);
            _pickPending[f0] = true;
            _pickFrame[f0] = Counters.FrameIndex;
        }

        {
            uint off = colourOff;
            var (r, g, b) = plan.Clear;
            clears[0] = new VkClearValue(r, g, b, plan.Transparent ? 0f : 1f);
            clears[1] = new VkClearValue(1f, 0u);
            var rbi = new VkRenderPassBeginInfo
            {
                renderPass = _rpColor, framebuffer = im.Framebuffer, renderArea = new VkRect2D(0, 0, (uint)im.Width, (uint)im.Height),
                clearValueCount = 2, pClearValues = clears,
            };
            api.vkCmdBeginRenderPass(cb, &rbi, VkSubpassContents.Inline);
            SetViewport(api, cb, im.Width, im.Height);
            offs[0] = off; offs[1] = tBase;
            api.vkCmdBindDescriptorSets(cb, VkPipelineBindPoint.Graphics, _layout, 0, 1, &set, 2, offs);
            int transform = 0;
            Scene3DPipeline state = (Scene3DPipeline)(-1);
            var tie = Scene3DDepthTie.None;
            SetTie(api, cb, tie);
            Scene3DBuffer bound = (Scene3DBuffer)(-1);
            bool envBound = false, lightBound = false;
            // what vertex binding 1 holds: 0 nothing yet, 1 the shade stream, 2 the Lit field's normals (brief 109)
            int binding1 = 0;
            for (int i = 0; i < plan.DrawCount; i++)
            {
                ref var d = ref plan.Draws[i];
                var buf = d.Buffer switch
                {
                    Scene3DBuffer.Scene => _vb.Buf, Scene3DBuffer.SceneLines => _lines.Buf, Scene3DBuffer.Field => _field.Buf,
                    Scene3DBuffer.Image => _imageVb.Buf,
                    Scene3DBuffer.Overlay0 => _overlays[0].Buf, Scene3DBuffer.Overlay1 => _overlays[1].Buf, _ => _overlays[2].Buf,
                };
                bool lines = d.Pipeline is Scene3DPipeline.Lines or Scene3DPipeline.Edges;
                bool field = d.Pipeline is Scene3DPipeline.Field or Scene3DPipeline.FieldBlend or Scene3DPipeline.FieldLit;
                bool fieldLit = d.Pipeline == Scene3DPipeline.FieldLit;
                // brief-em3d-106 — the backdrop draws like the grid; a realistic draw needs the environment's set and the table.
                bool grid = d.Pipeline is Scene3DPipeline.Grid or Scene3DPipeline.Backdrop or Scene3DPipeline.Ground;
                bool pbr = d.Pipeline is Scene3DPipeline.Pbr or Scene3DPipeline.PbrTranslucent;
                if ((pbr || d.Pipeline == Scene3DPipeline.Backdrop) && (_envSet.Handle == 0 || !_appearancesWritten)) continue;
                if (pbr && _shade.Buf.Handle == 0) continue;
                // brief-em3d-109 — a Lit field needs the environment for its sheen and its normals beside its vertices
                if (fieldLit && (_envSet.Handle == 0 || !_appearancesWritten || _fieldNormals.Buf.Handle == 0
                                 || d.First + d.Count > _fieldNormalCount)) continue;
                // brief-em3d-101 — an image draw: not indexed; its texture's set at set 1.
                bool isImage = d.Pipeline is Scene3DPipeline.Image or Scene3DPipeline.ImageTranslucent;
                var bound1 = _textures.Bound;
                if (isImage && (d.Texture < 0 || d.Texture >= bound1.Length || bound1[d.Texture] is null)) continue;
                if (!grid && (buf.Handle == 0 || (!lines && !field && !isImage && _ib.Buf.Handle == 0))) continue;
                if (field && d.First + d.Count > _fieldCount) continue;
                if (d.Tie != tie) SetTie(api, cb, tie = d.Tie);
                if (d.Pipeline != state)
                {
                    state = d.Pipeline;
                    api.vkCmdBindPipeline(cb, VkPipelineBindPoint.Graphics, state switch
                    {
                        Scene3DPipeline.Translucent => _pTrans, Scene3DPipeline.Lines => _pLines,
                        Scene3DPipeline.Field => _pField, Scene3DPipeline.FieldBlend => _pFieldBlend, Scene3DPipeline.FieldLit => _pFieldLit,
                        Scene3DPipeline.Edges => _pEdges, Scene3DPipeline.OnTop => _pTop, Scene3DPipeline.Grid => _pGrid,
                        Scene3DPipeline.Image => _pImage, Scene3DPipeline.ImageTranslucent => _pImageTrans,
                        Scene3DPipeline.Pbr => _pPbr, Scene3DPipeline.PbrTranslucent => _pPbrGlass, Scene3DPipeline.Backdrop => _pBackdrop,
                        Scene3DPipeline.Ground => _pGround, _ => _pOpaque,
                    });
                    if ((pbr || state == Scene3DPipeline.Ground) && !lightBound)
                    {
                        // brief-em3d-107 — set 3 (the shadow map, the occlusion), once a frame, as set 2 is
                        var ls = _lightSet;
                        api.vkCmdBindDescriptorSets(cb, VkPipelineBindPoint.Graphics, _layout, 3, 1, &ls, 0, null);
                        lightBound = true;
                    }
                    if ((pbr || state is Scene3DPipeline.Backdrop or Scene3DPipeline.FieldLit) && !envBound)
                    {
                        // brief-em3d-106 — set 2, once a frame: binding set 0 again (a transform) leaves it, the layouts being one.
                        var es = _envSet;
                        api.vkCmdBindDescriptorSets(cb, VkPipelineBindPoint.Graphics, _layout, 2, 1, &es, 0, null);
                        envBound = true;
                    }
                }
                if (isImage)
                {
                    var texSet = bound1[d.Texture]!.Set;
                    api.vkCmdBindDescriptorSets(cb, VkPipelineBindPoint.Graphics, _layout, 1, 1, &texSet, 0, null);
                }
                if (grid)
                {
                    api.vkCmdDraw(cb, 6, 1, 0, 0);
                    draws++;
                    continue;
                }
                if (d.Transform != transform && d.Transform < tCount)
                {
                    // brief-em3d-46 — a drag's preview: the same set, re-offset to this draw's transform.
                    transform = d.Transform;
                    offs[1] = tBase + (uint)(transform * TransformStride);
                    api.vkCmdBindDescriptorSets(cb, VkPipelineBindPoint.Graphics, _layout, 0, 1, &set, 2, offs);
                }
                if (d.Buffer != bound)
                {
                    bound = d.Buffer;
                    api.vkCmdBindVertexBuffers(cb, 0, 1, &buf, &zero);
                    if (!lines && !field && !isImage) api.vkCmdBindIndexBuffer(cb, _ib.Buf, 0, VkIndexType.Uint32);
                }
                if (pbr && binding1 != 1)
                {
                    // brief-em3d-106 — the shade stream at vertex binding 1; only it and the Lit field's normals use binding 1.
                    var sb = _shade.Buf;
                    api.vkCmdBindVertexBuffers(cb, 1, 1, &sb, &zero);
                    binding1 = 1;
                }
                if (fieldLit && binding1 != 2)
                {
                    var nb = _fieldNormals.Buf;
                    api.vkCmdBindVertexBuffers(cb, 1, 1, &nb, &zero);
                    binding1 = 2;
                }
                if (lines || field || isImage) api.vkCmdDraw(cb, (uint)d.Count, 1, (uint)d.First, 0);
                else api.vkCmdDrawIndexed(cb, (uint)d.Count, 1, (uint)d.First, 0, 0);
                draws++;
            }
            api.vkCmdEndRenderPass(cb);
        }
        Check(api.vkEndCommandBuffer(cb), "vkEndCommandBuffer");

        var waitStage = VkPipelineStageFlags.ColorAttachmentOutput;
        var si = new VkSubmitInfo
        {
            waitSemaphoreCount = wait.Handle != 0 ? 1u : 0u, pWaitSemaphores = &wait, pWaitDstStageMask = &waitStage,
            commandBufferCount = 1, pCommandBuffers = &cb,
            signalSemaphoreCount = signal.Handle != 0 ? 1u : 0u, pSignalSemaphores = &signal,
        };
        Check(api.vkQueueSubmit(_queue, 1, &si, _fence[f0]), "vkQueueSubmit");
        if (signal.Handle != 0) im.ReadySignalled = true;
        _submitted[f0] = true;
        _frame++;
        DrawCallsLastFrame = draws;
    }

    /// <summary>R-em3d107-1 — the casters into the key light's map, with the shadow bias (dynamic, as every tie's is).</summary>
    private int ShadowPass(VkDeviceApi api, VkCommandBuffer cb, Scene3DFramePlan plan, VkDescriptorSet set, uint uniformOff, uint tBase, int tCount)
    {
        var map = _shadowMap!;
        var clear = new VkClearValue(1f, 0u);
        var rbi = new VkRenderPassBeginInfo
        {
            renderPass = _rpShadow, framebuffer = map.Framebuffer, renderArea = new VkRect2D(0, 0, (uint)map.Size, (uint)map.Size),
            clearValueCount = 1, pClearValues = &clear,
        };
        api.vkCmdBeginRenderPass(cb, &rbi, VkSubpassContents.Inline);
        SetViewport(api, cb, map.Size, map.Size);
        api.vkCmdBindPipeline(cb, VkPipelineBindPoint.Graphics, _pShadow);
        var offs = stackalloc uint[2] { uniformOff, tBase };
        api.vkCmdBindDescriptorSets(cb, VkPipelineBindPoint.Graphics, _layout, 0, 1, &set, 2, offs);
        var (constant, slope, clamp) = Scene3DFramePlan.ShadowBias;
        api.vkCmdSetDepthBias(cb, constant, clamp, slope);
        ulong zero = 0;
        var vb = _vb.Buf;
        api.vkCmdBindVertexBuffers(cb, 0, 1, &vb, &zero);
        api.vkCmdBindIndexBuffer(cb, _ib.Buf, 0, VkIndexType.Uint32);
        int transform = 0, draws = 0;
        for (int i = 0; i < plan.ShadowDrawCount; i++)
        {
            ref var d = ref plan.ShadowDraws[i];
            if (d.Transform != transform && d.Transform < tCount)
            {
                transform = d.Transform;
                offs[1] = tBase + (uint)(transform * TransformStride);
                api.vkCmdBindDescriptorSets(cb, VkPipelineBindPoint.Graphics, _layout, 0, 1, &set, 2, offs);
            }
            api.vkCmdDrawIndexed(cb, (uint)d.Count, 1, (uint)d.First, 0, 0);
            draws++;
        }
        api.vkCmdEndRenderPass(cb);
        return draws;
    }

    /// <summary>R-em3d107-2 — the occlusion: the opaque materials' (and the ground's) depths along the view's rays into its own targets,
    /// the horizon pass, the blur. Each pass leaves its target SHADER_READ_ONLY_OPTIMAL for the next.</summary>
    private int OcclusionPasses(VkDeviceApi api, VkCommandBuffer cb, Scene3DFramePlan plan, VkDescriptorSet set, uint uniformOff, uint tBase, int tCount)
    {
        var o = _occlusion!;
        var clears = stackalloc VkClearValue[2];
        clears[0] = new VkClearValue(CircuitRF.Render.Scene3D.Look.Occlusion.Empty, 0f, 0f, 0f);
        clears[1] = new VkClearValue(1f, 0u);
        var rbi = new VkRenderPassBeginInfo
        {
            renderPass = _rpPrepass, framebuffer = o.Prepass, renderArea = new VkRect2D(0, 0, (uint)o.Width, (uint)o.Height),
            clearValueCount = 2, pClearValues = clears,
        };
        api.vkCmdBeginRenderPass(cb, &rbi, VkSubpassContents.Inline);
        SetViewport(api, cb, o.Width, o.Height);
        api.vkCmdBindPipeline(cb, VkPipelineBindPoint.Graphics, _pPrepass);
        var offs = stackalloc uint[2] { uniformOff, tBase };
        api.vkCmdBindDescriptorSets(cb, VkPipelineBindPoint.Graphics, _layout, 0, 1, &set, 2, offs);
        SetTie(api, cb, Scene3DDepthTie.None);
        ulong zero = 0;
        var vb = _vb.Buf;
        api.vkCmdBindVertexBuffers(cb, 0, 1, &vb, &zero);
        api.vkCmdBindIndexBuffer(cb, _ib.Buf, 0, VkIndexType.Uint32);
        int transform = 0, draws = 0;
        for (int i = 0; i < plan.DrawCount; i++)
        {
            ref var d = ref plan.Draws[i];
            if (d.Pipeline != Scene3DPipeline.Pbr) continue;
            if (d.Transform != transform && d.Transform < tCount)
            {
                transform = d.Transform;
                offs[1] = tBase + (uint)(transform * TransformStride);
                api.vkCmdBindDescriptorSets(cb, VkPipelineBindPoint.Graphics, _layout, 0, 1, &set, 2, offs);
            }
            api.vkCmdDrawIndexed(cb, (uint)d.Count, 1, (uint)d.First, 0, 0);
            draws++;
        }
        if (plan.GroundDrawn)
        {
            api.vkCmdBindPipeline(cb, VkPipelineBindPoint.Graphics, _pGroundPrepass);
            api.vkCmdDraw(cb, 6, 1, 0, 0);
            draws++;
        }
        api.vkCmdEndRenderPass(cb);

        var ls = _lightSet;
        foreach (var (pipe, fb) in new[] { (_pAo, o.AoPass), (_pAoBlur, o.BlurPass) })
        {
            var abi = new VkRenderPassBeginInfo
            {
                renderPass = _rpAo, framebuffer = fb, renderArea = new VkRect2D(0, 0, (uint)o.Width, (uint)o.Height), clearValueCount = 0,
            };
            api.vkCmdBeginRenderPass(cb, &abi, VkSubpassContents.Inline);
            SetViewport(api, cb, o.Width, o.Height);
            api.vkCmdBindPipeline(cb, VkPipelineBindPoint.Graphics, pipe);
            api.vkCmdBindDescriptorSets(cb, VkPipelineBindPoint.Graphics, _layout, 3, 1, &ls, 0, null);
            api.vkCmdDraw(cb, 6, 1, 0, 0);
            draws++;
            api.vkCmdEndRenderPass(cb);
        }
        return draws;
    }

    private static void SetViewport(VkDeviceApi api, VkCommandBuffer cb, int w, int h)
    {
        var view = new VkViewport { x = 0, y = 0, width = w, height = h, minDepth = 0, maxDepth = 1 };
        var sc = new VkRect2D(0, 0, (uint)w, (uint)h);
        api.vkCmdSetViewport(cb, 0, 1, &view);
        api.vkCmdSetScissor(cb, 0, 1, &sc);
    }

    private static void CopyTexel(VkDeviceApi api, VkCommandBuffer cb, VkImage img, VkBuffer buf, ulong offset)
    {
        var region = new VkBufferImageCopy
        {
            bufferOffset = offset,
            imageSubresource = new VkImageSubresourceLayers { aspectMask = VkImageAspectFlags.Color, layerCount = 1 },
            imageExtent = new VkExtent3D { width = 1, height = 1, depth = 1 },
        };
        api.vkCmdCopyImageToBuffer(cb, img, VkImageLayout.TransferSrcOptimal, buf, 1, &region);
    }

    /// <summary>Non-blocking: a slot's texels are read only once its fence has signalled.</summary>
    private void CollectPicks(VkDeviceApi api)
    {
        for (int k = 1; k <= Ring; k++)
        {
            int s = (int)((_frame + k) % Ring);   // oldest first
            if (!_pickPending[s] || !_submitted[s]) continue;
            if (api.vkGetFenceStatus(_fence[s]) != VkResult.Success) continue;
            ReadPick(s);
        }
    }

    private void ReadPick(int s)
    {
        if (!_pickPending[s]) return;
        byte* p = _pickMapped[s];
        PickedId = *(uint*)p;
        PickedFace = ((uint*)p)[1];
        float* w = (float*)(p + 16);
        PickedPoint = new Vector3(w[0], w[1], w[2]);
        PickedSomething = w[3] > 0.5f;
        _pickPending[s] = false;
        Counters.PickResolved(_pickFrame[s]);
    }

    /// <summary>The headless path: waits for the GPU and reads image <paramref name="image"/> (left in
    /// TRANSFER_SRC_OPTIMAL by its frame) as RGBA rows, top row first.</summary>
    internal byte[] ReadRgba(int image) => ReadRgba(_images[image]);

    private byte[] ReadRgba(Image im)
    {
        var api = Api;
        Check(api.vkDeviceWaitIdle(), "vkDeviceWaitIdle");
        for (int s = 0; s < Ring; s++) ReadPick(s);
        long size = (long)im.Width * im.Height * 4;
        var buf = NewBuffer(api, size, VkBufferUsageFlags.TransferDst, VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent);
        OneShot(api, cb =>
        {
            var region = new VkBufferImageCopy
            {
                imageSubresource = new VkImageSubresourceLayers { aspectMask = VkImageAspectFlags.Color, layerCount = 1 },
                imageExtent = new VkExtent3D { width = (uint)im.Width, height = (uint)im.Height, depth = 1 },
            };
            api.vkCmdCopyImageToBuffer(cb, im.Color.Image, VkImageLayout.TransferSrcOptimal, buf.Item1, 1, &region);
        });
        void* p;
        Check(api.vkMapMemory(buf.Item2, 0, (ulong)size, 0, &p), "vkMapMemory");
        var px = new byte[size];
        Marshal.Copy((nint)p, px, 0, px.Length);
        api.vkUnmapMemory(buf.Item2);
        Free(api, buf);
        return px;
    }

    public override void Dispose()
    {
        if (_api is not { } api)
        {
            _vi?.vkDestroyInstance(null);      // a device that failed to come up after its instance did
            _vi = null;
            return;
        }
        api.vkDeviceWaitIdle();
        ReleaseImages();
        Free(api, _vb); Free(api, _ib); Free(api, _lines); Free(api, _field); Free(api, _imageVb); Free(api, _shade); Free(api, _fieldNormals);
        ReleaseEnvironmentTextures();
        api.vkUnmapMemory(_apb.Mem);
        Free(api, _apb);
        _textures.Clear(ReleaseTexture);
        foreach (var o in _overlays) Free(api, o);
        foreach (var b in _pickBuf) Free(api, b);
        Free(api, _ub);
        Free(api, _tb);
        Destroy(api, _pickId); Destroy(api, _pickPos); Destroy(api, _pickDepth);
        if (_pickFb.Handle != 0) api.vkDestroyFramebuffer(_pickFb, null);
        foreach (var f in _fence) if (f.Handle != 0) api.vkDestroyFence(f, null);
        api.vkDestroyPipeline(_pOpaque, null); api.vkDestroyPipeline(_pTrans, null);

        api.vkDestroyPipeline(_pLines, null); api.vkDestroyPipeline(_pPick, null); api.vkDestroyPipeline(_pField, null);
        api.vkDestroyPipeline(_pEdges, null); api.vkDestroyPipeline(_pTop, null); api.vkDestroyPipeline(_pGrid, null);
        api.vkDestroyPipeline(_pImage, null); api.vkDestroyPipeline(_pImageTrans, null);
        api.vkDestroyPipeline(_pPbr, null); api.vkDestroyPipeline(_pPbrTrans, null); api.vkDestroyPipeline(_pBackdrop, null);
        api.vkDestroyPipeline(_pFieldBlend, null); api.vkDestroyPipeline(_pFieldLit, null);
        DestroyShadowMap(api); DestroyOcclusion(api);
        Destroy(api, _noShadowMap); Destroy(api, _noOcclusion);
        foreach (var p in new[] { _pShadow, _pPrepass, _pGroundPrepass, _pAo, _pAoBlur, _pGround, _pPbrGlass }) api.vkDestroyPipeline(p, null);
        api.vkDestroyDescriptorPool(_lightPool, null); api.vkDestroyDescriptorSetLayout(_lightSetLayout, null);
        api.vkDestroySampler(_shadowSampler, null);
        api.vkDestroyRenderPass(_rpShadow, null); api.vkDestroyRenderPass(_rpPrepass, null); api.vkDestroyRenderPass(_rpAo, null);
        api.vkDestroyDescriptorSetLayout(_envSetLayout, null);
        api.vkDestroySampler(_sampler, null); api.vkDestroyDescriptorSetLayout(_imageSetLayout, null);
        api.vkDestroyPipelineLayout(_layout, null); api.vkDestroyDescriptorPool(_pool, null);
        api.vkDestroyDescriptorSetLayout(_setLayout, null); api.vkDestroyShaderModule(_module, null);
        api.vkDestroyRenderPass(_rpColor, null); api.vkDestroyRenderPass(_rpPick, null);
        api.vkDestroyCommandPool(_cmdPool, null);
        api.vkDestroyDevice(null);
        _vi?.vkDestroyInstance(null);
        _api = null;
    }
}
