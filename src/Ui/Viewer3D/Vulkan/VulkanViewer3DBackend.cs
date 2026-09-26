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
// Per frame the 112-byte uniform block is copied into a persistently-mapped, host-coherent ring and
// bound as a dynamic uniform buffer — counted as uniform bytes, never geometry. Three frames in flight,
// each with its own command buffer, fence and 1×1 pick readback buffer, read when its fence has
// signalled, never waited for.

using System.Numerics;
using System.Runtime.InteropServices;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using CircuitRF.Render.Scene3D;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace CircuitRF.Ui.Viewer3D.Vulkan;

internal sealed unsafe class VulkanViewer3DBackend : Viewer3DBackend
{
    private const int Ring = 3;
    private const int UniformStride = 256;    // ≥ 112 and a multiple of every minUniformBufferOffsetAlignment (≤ 256)
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
    private VkPhysicalDeviceMemoryProperties _mem;
    private string _description = "Vulkan (no device yet)";

    private VkRenderPass _rpColor, _rpPick;
    private VkDescriptorSetLayout _setLayout;
    private VkPipelineLayout _layout;
    private VkDescriptorPool _pool;
    private VkDescriptorSet _set;
    private VkShaderModule _module;
    private VkPipeline _pOpaque, _pTrans, _pLines, _pPick;
    private VkCommandPool _cmdPool;
    private (VkBuffer Buf, VkDeviceMemory Mem) _ub;
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
    private (VkBuffer Buf, VkDeviceMemory Mem) _vb, _ib, _lines;
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

        var binding = new VkDescriptorSetLayoutBinding
        {
            binding = 0, descriptorType = VkDescriptorType.UniformBufferDynamic, descriptorCount = 1,
            stageFlags = VkShaderStageFlags.Vertex | VkShaderStageFlags.Fragment,
        };
        var dsl = new VkDescriptorSetLayoutCreateInfo { bindingCount = 1, pBindings = &binding };
        VkDescriptorSetLayout setLayout;
        Check(api.vkCreateDescriptorSetLayout(&dsl, null, &setLayout), "vkCreateDescriptorSetLayout");
        _setLayout = setLayout;
        var pli = new VkPipelineLayoutCreateInfo { setLayoutCount = 1, pSetLayouts = &setLayout };
        VkPipelineLayout layout;
        Check(api.vkCreatePipelineLayout(&pli, null, &layout), "vkCreatePipelineLayout");
        _layout = layout;

        _pOpaque = Pipeline(api, _rpColor, "fs_color"u8, VkPrimitiveTopology.TriangleList, blend: false, depthWrite: true, targets: 1);
        _pTrans = Pipeline(api, _rpColor, "fs_color"u8, VkPrimitiveTopology.TriangleList, blend: true, depthWrite: false, targets: 1);
        _pLines = Pipeline(api, _rpColor, "fs_line"u8, VkPrimitiveTopology.LineList, blend: false, depthWrite: true, targets: 1);
        _pPick = Pipeline(api, _rpPick, "fs_pick"u8, VkPrimitiveTopology.TriangleList, blend: false, depthWrite: true, targets: 2);

        // two uniform blocks (pick, colour) per frame slot — host-coherent, mapped once
        _ub = NewBuffer(api, Ring * 2 * UniformStride, VkBufferUsageFlags.UniformBuffer, VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent);
        void* um;
        Check(api.vkMapMemory(_ub.Mem, 0, VK_WHOLE_SIZE, 0, &um), "vkMapMemory");
        _uMapped = (byte*)um;
        var ps = new VkDescriptorPoolSize { type = VkDescriptorType.UniformBufferDynamic, descriptorCount = 1 };
        var dpi = new VkDescriptorPoolCreateInfo { maxSets = 1, poolSizeCount = 1, pPoolSizes = &ps };
        VkDescriptorPool dp;
        Check(api.vkCreateDescriptorPool(&dpi, null, &dp), "vkCreateDescriptorPool");
        _pool = dp;
        var dai = new VkDescriptorSetAllocateInfo { descriptorPool = dp, descriptorSetCount = 1, pSetLayouts = &setLayout };
        VkDescriptorSet set;
        Check(api.vkAllocateDescriptorSets(&dai, &set), "vkAllocateDescriptorSets");
        _set = set;
        var dbi = new VkDescriptorBufferInfo { buffer = _ub.Buf, offset = 0, range = Scene3DFramePlan.UniformBytes };
        var w = new VkWriteDescriptorSet { dstSet = set, dstBinding = 0, descriptorCount = 1, descriptorType = VkDescriptorType.UniformBufferDynamic, pBufferInfo = &dbi };
        api.vkUpdateDescriptorSets(1, &w, 0, null);

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
        _pickId = NewImage(api, 1, 1, VkFormat.R32Uint, VkImageUsageFlags.ColorAttachment | VkImageUsageFlags.TransferSrc, VkImageAspectFlags.Color, export: false);
        _pickPos = NewImage(api, 1, 1, VkFormat.R32G32B32A32Sfloat, VkImageUsageFlags.ColorAttachment | VkImageUsageFlags.TransferSrc, VkImageAspectFlags.Color, export: false);
        _pickDepth = NewImage(api, 1, 1, DepthFormat, VkImageUsageFlags.DepthStencilAttachment, VkImageAspectFlags.Depth, export: false);
        _pickFb = Framebuffer(api, _rpPick, [_pickId.View, _pickPos.View, _pickDepth.View], 1, 1);
    }

    private VkRenderPass RenderPass(VkDeviceApi api, bool pick)
    {
        int colors = pick ? 2 : 1;
        var att = stackalloc VkAttachmentDescription[3];
        for (int i = 0; i < colors; i++)
            att[i] = new VkAttachmentDescription
            {
                format = pick ? (i == 0 ? VkFormat.R32Uint : VkFormat.R32G32B32A32Sfloat) : ColorFormat,
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
            srcAccessMask = VkAccessFlags.None,
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

    private VkPipeline Pipeline(VkDeviceApi api, VkRenderPass rp, ReadOnlySpan<byte> fragmentEntry, VkPrimitiveTopology topology,
                                bool blend, bool depthWrite, int targets)
    {
        fixed (byte* vsName = "vs"u8)
        fixed (byte* fsName = fragmentEntry)
        {
            var stages = stackalloc VkPipelineShaderStageCreateInfo[2];
            stages[0] = new VkPipelineShaderStageCreateInfo { stage = VkShaderStageFlags.Vertex, module = _module, pName = vsName };
            stages[1] = new VkPipelineShaderStageCreateInfo { stage = VkShaderStageFlags.Fragment, module = _module, pName = fsName };
            var vbd = new VkVertexInputBindingDescription { binding = 0, stride = Scene3DVertex.Stride, inputRate = VkVertexInputRate.Vertex };
            var attrs = stackalloc VkVertexInputAttributeDescription[3];
            attrs[0] = new VkVertexInputAttributeDescription { location = 0, binding = 0, format = VkFormat.R32G32B32Sfloat, offset = 0 };
            attrs[1] = new VkVertexInputAttributeDescription { location = 1, binding = 0, format = VkFormat.R32Uint, offset = 12 };
            attrs[2] = new VkVertexInputAttributeDescription { location = 2, binding = 0, format = VkFormat.R8G8B8A8Unorm, offset = 16 };
            var vin = new VkPipelineVertexInputStateCreateInfo
            {
                vertexBindingDescriptionCount = 1, pVertexBindingDescriptions = &vbd,
                vertexAttributeDescriptionCount = 3, pVertexAttributeDescriptions = attrs,
            };
            var ia = new VkPipelineInputAssemblyStateCreateInfo { topology = topology };
            var vp = new VkPipelineViewportStateCreateInfo { viewportCount = 1, scissorCount = 1 };
            // cull none; counter-clockwise front faces — see the header on why FlipY is false
            var rs = new VkPipelineRasterizationStateCreateInfo
            {
                polygonMode = VkPolygonMode.Fill, cullMode = VkCullModeFlags.None, frontFace = VkFrontFace.CounterClockwise, lineWidth = 1f,
            };
            var ms = new VkPipelineMultisampleStateCreateInfo { rasterizationSamples = VkSampleCountFlags.Count1 };
            var ds = new VkPipelineDepthStencilStateCreateInfo { depthTestEnable = true, depthWriteEnable = depthWrite, depthCompareOp = VkCompareOp.LessOrEqual };
            var cba = stackalloc VkPipelineColorBlendAttachmentState[2];
            for (int i = 0; i < targets; i++)
                cba[i] = new VkPipelineColorBlendAttachmentState
                {
                    blendEnable = blend,
                    srcColorBlendFactor = VkBlendFactor.SrcAlpha, dstColorBlendFactor = VkBlendFactor.OneMinusSrcAlpha, colorBlendOp = VkBlendOp.Add,
                    // the shared image's alpha stays 1 (R-em3d28-1d)
                    srcAlphaBlendFactor = VkBlendFactor.One, dstAlphaBlendFactor = VkBlendFactor.OneMinusSrcAlpha, alphaBlendOp = VkBlendOp.Add,
                    colorWriteMask = VkColorComponentFlags.All,
                };
            var cb = new VkPipelineColorBlendStateCreateInfo { attachmentCount = (uint)targets, pAttachments = cba };
            var dyn = stackalloc VkDynamicState[2] { VkDynamicState.Viewport, VkDynamicState.Scissor };
            var dy = new VkPipelineDynamicStateCreateInfo { dynamicStateCount = 2, pDynamicStates = dyn };
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
        VkDeviceMemory m;
        Check(api.vkAllocateMemory(&mai, null, &m), "vkAllocateMemory");
        Check(api.vkBindBufferMemory(b, m, 0), "vkBindBufferMemory");
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
            mipLevels = 1, arrayLayers = 1, samples = VkSampleCountFlags.Count1, tiling = VkImageTiling.Optimal,
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
    }

    public override void UploadOverlay(Scene3DBuffer slot, Scene3DVertex[] lines)
    {
        var api = Api;
        int i = slot - Scene3DBuffer.Overlay0;
        Free(api, ref _overlays[i]);
        fixed (Scene3DVertex* p = lines) _overlays[i] = Upload(api, p, lines.Length * Scene3DVertex.Stride, VkBufferUsageFlags.VertexBuffer);
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
        var bi = new VkCommandBufferBeginInfo { flags = VkCommandBufferUsageFlags.OneTimeSubmit };
        Check(api.vkBeginCommandBuffer(cb, &bi), "vkBeginCommandBuffer");
        record(cb);
        Check(api.vkEndCommandBuffer(cb), "vkEndCommandBuffer");
        var si = new VkSubmitInfo { commandBufferCount = 1, pCommandBuffers = &cb };
        Check(api.vkQueueSubmit(_queue, 1, &si, VkFence.Null), "vkQueueSubmit");
        Check(api.vkQueueWaitIdle(_queue), "vkQueueWaitIdle");
        api.vkFreeCommandBuffers(_cmdPool, 1, &cb);
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
        foreach (var im in _images)
        {
            im.Pending?.Wait(250);
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
        if (o is IAsyncDisposable ad) _ = ad.DisposeAsync();
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
        im.Pending = surface.UpdateWithSemaphoresAsync(im.Imported, im.ReadyImported!, im.ReleasedImported!);
        im.ReleaseOwed = true;
        im.ReadySignalled = false;
    }

    // ── the frame ───────────────────────────────────────────────────────────────────────────

    public override void Render(int image, Scene3DFramePlan plan, ulong frame)
    {
        var api = Api;
        var im = _images[image];
        VkSemaphore wait = default, signal = default;
        if (im.ReleaseOwed)
        {
            // a binary semaphore may be waited only once its signal is submitted: the update must be done
            if (im.Pending is { IsCompleted: false })
                throw new Viewer3DPresentFault($"The compositor has not finished with the 3D view's image {image}, so its release cannot be waited for.");
            if (im.Pending is { IsFaulted: true } f)
                throw new Viewer3DPresentFault($"The compositor's update of the 3D view's image {image} failed: {f.Exception?.GetBaseException().Message}");
            wait = im.Released;
            im.ReleaseOwed = false;
        }
        if (im.Ready.Handle != 0 && !im.ReadySignalled) signal = im.Ready;

        int f0 = (int)(_frame % Ring);
        CollectPicks(api);
        if (_submitted[f0])
        {
            var fence = _fence[f0];
            Check(api.vkWaitForFences(1, &fence, true, ulong.MaxValue), "vkWaitForFences");
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
            api.vkCmdBindDescriptorSets(cb, VkPipelineBindPoint.Graphics, _layout, 0, 1, &set, 1, &off);
            var vb = _vb.Buf;
            api.vkCmdBindVertexBuffers(cb, 0, 1, &vb, &zero);
            api.vkCmdBindIndexBuffer(cb, _ib.Buf, 0, VkIndexType.Uint32);
            for (int i = 0; i < plan.PickDrawCount; i++)
            {
                ref var d = ref plan.PickDraws[i];
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
            uint off = (uint)((f0 * 2 + 1) * UniformStride);
            fixed (float* u = plan.Uniforms) Buffer.MemoryCopy(u, _uMapped + off, Scene3DFramePlan.UniformBytes, Scene3DFramePlan.UniformBytes);
            Counters.CountUniform(Scene3DFramePlan.UniformBytes);
            var (r, g, b) = plan.Clear;
            clears[0] = new VkClearValue(r, g, b, 1f);
            clears[1] = new VkClearValue(1f, 0u);
            var rbi = new VkRenderPassBeginInfo
            {
                renderPass = _rpColor, framebuffer = im.Framebuffer, renderArea = new VkRect2D(0, 0, (uint)im.Width, (uint)im.Height),
                clearValueCount = 2, pClearValues = clears,
            };
            api.vkCmdBeginRenderPass(cb, &rbi, VkSubpassContents.Inline);
            SetViewport(api, cb, im.Width, im.Height);
            api.vkCmdBindDescriptorSets(cb, VkPipelineBindPoint.Graphics, _layout, 0, 1, &set, 1, &off);
            Scene3DPipeline state = (Scene3DPipeline)(-1);
            Scene3DBuffer bound = (Scene3DBuffer)(-1);
            for (int i = 0; i < plan.DrawCount; i++)
            {
                ref var d = ref plan.Draws[i];
                var buf = d.Buffer switch
                {
                    Scene3DBuffer.Scene => _vb.Buf, Scene3DBuffer.SceneLines => _lines.Buf,
                    Scene3DBuffer.Overlay0 => _overlays[0].Buf, Scene3DBuffer.Overlay1 => _overlays[1].Buf, _ => _overlays[2].Buf,
                };
                bool lines = d.Pipeline == Scene3DPipeline.Lines;
                if (buf.Handle == 0 || (!lines && _ib.Buf.Handle == 0)) continue;
                if (d.Pipeline != state)
                {
                    state = d.Pipeline;
                    api.vkCmdBindPipeline(cb, VkPipelineBindPoint.Graphics, state switch
                    {
                        Scene3DPipeline.Translucent => _pTrans, Scene3DPipeline.Lines => _pLines, _ => _pOpaque,
                    });
                }
                if (d.Buffer != bound)
                {
                    bound = d.Buffer;
                    api.vkCmdBindVertexBuffers(cb, 0, 1, &buf, &zero);
                    if (!lines) api.vkCmdBindIndexBuffer(cb, _ib.Buf, 0, VkIndexType.Uint32);
                }
                if (lines) api.vkCmdDraw(cb, (uint)d.Count, 1, (uint)d.First, 0);
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
        float* w = (float*)(p + 16);
        PickedPoint = new Vector3(w[0], w[1], w[2]);
        PickedSomething = w[3] > 0.5f;
        _pickPending[s] = false;
        Counters.PickResolved(_pickFrame[s]);
    }

    /// <summary>The headless path: waits for the GPU and reads image <paramref name="image"/> (left in
    /// TRANSFER_SRC_OPTIMAL by its frame) as RGBA rows, top row first.</summary>
    internal byte[] ReadRgba(int image)
    {
        var api = Api;
        var im = _images[image];
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
        if (_api is not { } api) return;
        api.vkDeviceWaitIdle();
        ReleaseImages();
        Free(api, _vb); Free(api, _ib); Free(api, _lines);
        foreach (var o in _overlays) Free(api, o);
        foreach (var b in _pickBuf) Free(api, b);
        Free(api, _ub);
        Destroy(api, _pickId); Destroy(api, _pickPos); Destroy(api, _pickDepth);
        if (_pickFb.Handle != 0) api.vkDestroyFramebuffer(_pickFb, null);
        foreach (var f in _fence) if (f.Handle != 0) api.vkDestroyFence(f, null);
        api.vkDestroyPipeline(_pOpaque, null); api.vkDestroyPipeline(_pTrans, null);
        api.vkDestroyPipeline(_pLines, null); api.vkDestroyPipeline(_pPick, null);
        api.vkDestroyPipelineLayout(_layout, null); api.vkDestroyDescriptorPool(_pool, null);
        api.vkDestroyDescriptorSetLayout(_setLayout, null); api.vkDestroyShaderModule(_module, null);
        api.vkDestroyRenderPass(_rpColor, null); api.vkDestroyRenderPass(_rpPick, null);
        api.vkDestroyCommandPool(_cmdPool, null);
        api.vkDestroyDevice(null);
        _vi?.vkDestroyInstance(null);
        _api = null;
    }
}
