using System.Runtime.InteropServices;
using Viewer3dSpike.Scene;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace Viewer3dSpike.Render.Vk;

/// <summary>One render target: an R8G8B8A8 image, optionally EXPORTABLE as an opaque POSIX file
/// descriptor (the Linux pane hands that fd to Avalonia's compositor), with its own depth buffer and
/// framebuffer. Created exactly as Avalonia's own importer re-creates it from the fd — format, tiling,
/// usage, MUTABLE_FORMAT, one mip, a DEDICATED allocation — because an opaque handle only imports into
/// an image with identical creation parameters.</summary>
public sealed class VulkanTarget
{
    public VkImage Image;
    public VkDeviceMemory Memory;
    public ulong MemorySize;
    public VkImageView View;
    public VkImage Depth;
    public VkDeviceMemory DepthMemory;
    public VkImageView DepthView;
    public VkFramebuffer Framebuffer;
    public int Width, Height;
    public bool Exportable;
}

/// <summary>
/// Route A on Linux (brief em3d-28 step 0): native Vulkan through Vortice.Vulkan's managed bindings (MIT;
/// the loader, libvulkan.so.1, is the system's). The shader is the ONE WGSL source cross-compiled offline
/// by tools/ShaderGen to <c>shaders/scene.spv</c> — one module, three entry points.
///
/// Same scene and draw structure as the other routes. Three frames in flight, each with its own command
/// buffer, fence, 1×1 pick readback buffer and slice of a persistently-mapped uniform buffer (608 bytes
/// per pass, bound as a dynamic uniform — counted as uniform bytes, never geometry). A pick is read when
/// its frame's fence has signalled, checked without waiting.
///
/// Every frame leaves the target in TRANSFER_SRC_OPTIMAL: that is the layout both Avalonia importers
/// assume (the Vulkan compositor transitions an imported image to it once and reads it from there; the
/// GL one waits its semaphore with GL_LAYOUT_TRANSFER_SRC_EXT).
/// </summary>
public sealed unsafe class VulkanRenderer : IDisposable
{
    const int Ring = 3;
    public const int UniformBytes = 96 + 512;
    const int UniformStride = 768;                 // ≥ 608, a multiple of every minUniformBufferOffsetAlignment (≤ 256)
    public const VkFormat ColorFormat = VkFormat.R8G8B8A8Unorm;
    const VkFormat DepthFormat = VkFormat.D32Sfloat;
    const VkImageUsageFlags TargetUsage = VkImageUsageFlags.TransferSrc | VkImageUsageFlags.TransferDst | VkImageUsageFlags.Sampled | VkImageUsageFlags.ColorAttachment;

    readonly FrameCounters _counters;
    public readonly VkInstance Instance;
    public readonly VkPhysicalDevice Physical;
    public readonly VkDevice Device;
    public readonly VkQueue Queue;
    public readonly uint QueueFamily;
    readonly VkInstanceApi _vi;
    public readonly VkDeviceApi Api;
    public string Info { get; }
    public readonly bool CanExport;
    public readonly byte[] DeviceUuid = new byte[16];
    VkPhysicalDeviceMemoryProperties _mem;

    VkRenderPass _rpColor, _rpPick;
    VkPipelineLayout _layout;
    VkDescriptorSetLayout _setLayout;
    VkDescriptorPool _pool;
    VkDescriptorSet _set;
    VkPipeline _pOpaque, _pTrans, _pPick;
    VkShaderModule _module;
    VkCommandPool _cmdPool;
    (VkBuffer Buf, VkDeviceMemory Mem) _vb, _ib, _ub;
    byte* _uMapped;
    readonly VkCommandBuffer[] _cmd = new VkCommandBuffer[Ring];
    readonly VkFence[] _fence = new VkFence[Ring];
    readonly bool[] _submitted = new bool[Ring];
    readonly (VkBuffer Buf, VkDeviceMemory Mem)[] _pickBuf = new (VkBuffer, VkDeviceMemory)[Ring];
    readonly uint*[] _pickMapped = new uint*[Ring];
    readonly bool[] _pickPending = new bool[Ring];
    readonly long[] _pickFrame = new long[Ring];
    VkImage _pickImg, _pickDepthImg;
    VkDeviceMemory _pickImgMem, _pickDepthMem;
    VkImageView _pickView, _pickDepthView;
    VkFramebuffer _pickFb;
    long _frame;
    SceneModel _scene = null!;
    TranslucentSorter _sorter = null!;
    readonly float[] _ublock = GC.AllocateArray<float>(UniformBytes / 4, pinned: true);
    public uint HoveredId { get; private set; }
    public int DrawCallsLastFrame { get; private set; }

    /// <param name="deviceUuid">The compositor's GPU (ICompositionGpuInterop.DeviceUuid): an opaque fd only
    /// imports on the same physical device. Null: the first device with a graphics queue.</param>
    public VulkanRenderer(FrameCounters counters, byte[]? deviceUuid = null)
    {
        _counters = counters;
        if (vkInitialize() != VkResult.Success) throw new InvalidOperationException("no Vulkan loader (libvulkan.so.1 / vulkan-1.dll) on this machine");
        var app = new VkApplicationInfo { apiVersion = VkVersion.Version_1_1 };
        fixed (byte* name = "circuitRF viewer spike"u8) app.pApplicationName = name;
        var ici = new VkInstanceCreateInfo { pApplicationInfo = &app };
        VkInstance inst;
        Check(vkCreateInstance(&ici, &inst), "vkCreateInstance");
        Instance = inst;
        _vi = GetApi(Instance);

        uint n = 0;
        Check(_vi.vkEnumeratePhysicalDevices(&n, null), "vkEnumeratePhysicalDevices");
        var devs = stackalloc VkPhysicalDevice[(int)n];
        Check(_vi.vkEnumeratePhysicalDevices(&n, devs), "vkEnumeratePhysicalDevices");
        string all = "";
        for (int i = 0; i < n && Physical.Handle == 0; i++)
        {
            var id = new VkPhysicalDeviceIDProperties();
            var p2 = new VkPhysicalDeviceProperties2 { pNext = &id };
            _vi.vkGetPhysicalDeviceProperties2(devs[i], &p2);
            var uuid = new ReadOnlySpan<byte>(id.deviceUUID, 16).ToArray();
            all += (all.Length > 0 ? "; " : "") + Convert.ToHexString(uuid);
            if (deviceUuid != null && !uuid.AsSpan().SequenceEqual(deviceUuid)) continue;
            uint qn = 0;
            _vi.vkGetPhysicalDeviceQueueFamilyProperties(devs[i], &qn, null);
            var qf = stackalloc VkQueueFamilyProperties[(int)qn];
            _vi.vkGetPhysicalDeviceQueueFamilyProperties(devs[i], &qn, qf);
            for (uint q = 0; q < qn; q++)
                if ((qf[q].queueFlags & VkQueueFlags.Graphics) != 0)
                {
                    Physical = devs[i]; QueueFamily = q;
                    uuid.CopyTo(DeviceUuid, 0);
                    Info = "Vulkan: " + Marshal.PtrToStringUTF8((nint)p2.properties.deviceName) + (deviceUuid != null ? " — the compositor's device (UUID match)" : "");
                    break;
                }
        }
        if (Physical.Handle == 0)
            throw new InvalidOperationException(deviceUuid != null
                ? $"no Vulkan device has the compositor's UUID {Convert.ToHexString(deviceUuid)} (devices: {all})"
                : "no Vulkan device with a graphics queue");
        Info ??= "Vulkan";

        // the fd exports are what the pane needs; the harness runs without them
        uint en = 0;
        _vi.vkEnumerateDeviceExtensionProperties(Physical, null, &en, null);
        var ext = new VkExtensionProperties[en];
        fixed (VkExtensionProperties* pe = ext) _vi.vkEnumerateDeviceExtensionProperties(Physical, null, &en, pe);
        bool Has(string e) { foreach (var x in ext) if (Marshal.PtrToStringUTF8((nint)x.extensionName) == e) return true; return false; }
        CanExport = Has("VK_KHR_external_memory_fd") && Has("VK_KHR_external_semaphore_fd");

        float prio = 1f;
        var qci = new VkDeviceQueueCreateInfo { queueFamilyIndex = QueueFamily, queueCount = 1, pQueuePriorities = &prio };
        fixed (byte* e1 = "VK_KHR_external_memory_fd"u8)
        fixed (byte* e2 = "VK_KHR_external_semaphore_fd"u8)
        {
            byte** names = stackalloc byte*[2] { e1, e2 };
            var dci = new VkDeviceCreateInfo { queueCreateInfoCount = 1, pQueueCreateInfos = &qci, enabledExtensionCount = CanExport ? 2u : 0u, ppEnabledExtensionNames = names };
            VkDevice dev;
            Check(_vi.vkCreateDevice(Physical, &dci, null, &dev), "vkCreateDevice");
            Device = dev;
        }
        Api = GetApi(Instance, Device);
        VkQueue queue;
        Api.vkGetDeviceQueue(QueueFamily, 0, &queue);
        Queue = queue;
        VkPhysicalDeviceMemoryProperties mp;
        _vi.vkGetPhysicalDeviceMemoryProperties(Physical, &mp);
        _mem = mp;
        Info += CanExport ? " (fd export available)" : " (no fd export: harness only)";
    }

    static void Check(VkResult r, string what)
    {
        if (r != VkResult.Success) throw new InvalidOperationException($"{what} returned {r}");
    }

    uint MemoryType(uint bits, VkMemoryPropertyFlags want)
    {
        for (uint i = 0; i < _mem.memoryTypeCount; i++)
            if ((bits & (1u << (int)i)) != 0 && (_mem.memoryTypes[(int)i].propertyFlags & want) == want) return i;
        throw new InvalidOperationException($"no memory type {want} in mask {bits:X}");
    }

    (VkBuffer, VkDeviceMemory) NewBuffer(ulong size, VkBufferUsageFlags usage, VkMemoryPropertyFlags props)
    {
        var bci = new VkBufferCreateInfo { size = size, usage = usage, sharingMode = VkSharingMode.Exclusive };
        VkBuffer b;
        Check(Api.vkCreateBuffer(&bci, null, &b), "vkCreateBuffer");
        VkMemoryRequirements req;
        Api.vkGetBufferMemoryRequirements(b, &req);
        var mai = new VkMemoryAllocateInfo { allocationSize = req.size, memoryTypeIndex = MemoryType(req.memoryTypeBits, props) };
        VkDeviceMemory m;
        Check(Api.vkAllocateMemory(&mai, null, &m), "vkAllocateMemory");
        Check(Api.vkBindBufferMemory(b, m, 0), "vkBindBufferMemory");
        return (b, m);
    }

    /// <summary>Device-local geometry through a staging buffer; every byte counted once.</summary>
    (VkBuffer, VkDeviceMemory) Upload(void* data, int length, VkBufferUsageFlags usage)
    {
        _counters.CountUpload(length);
        var staging = NewBuffer((ulong)length, VkBufferUsageFlags.TransferSrc, VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent);
        void* p;
        Check(Api.vkMapMemory(staging.Item2, 0, (ulong)length, 0, &p), "vkMapMemory");
        Buffer.MemoryCopy(data, p, length, length);
        Api.vkUnmapMemory(staging.Item2);
        var dst = NewBuffer((ulong)length, usage | VkBufferUsageFlags.TransferDst, VkMemoryPropertyFlags.DeviceLocal);
        OneShot(cb =>
        {
            var region = new VkBufferCopy { size = (ulong)length };
            Api.vkCmdCopyBuffer(cb, staging.Item1, dst.Item1, 1, &region);
        });
        Api.vkDestroyBuffer(staging.Item1, null);
        Api.vkFreeMemory(staging.Item2, null);
        return dst;
    }

    void OneShot(Action<VkCommandBuffer> record)
    {
        var ai = new VkCommandBufferAllocateInfo { commandPool = _cmdPool, level = VkCommandBufferLevel.Primary, commandBufferCount = 1 };
        VkCommandBuffer cb;
        Check(Api.vkAllocateCommandBuffers(&ai, &cb), "vkAllocateCommandBuffers");
        var bi = new VkCommandBufferBeginInfo { flags = VkCommandBufferUsageFlags.OneTimeSubmit };
        Api.vkBeginCommandBuffer(cb, &bi);
        record(cb);
        Api.vkEndCommandBuffer(cb);
        var si = new VkSubmitInfo { commandBufferCount = 1, pCommandBuffers = &cb };
        Check(Api.vkQueueSubmit(Queue, 1, &si, VkFence.Null), "vkQueueSubmit");
        Api.vkQueueWaitIdle(Queue);
        Api.vkFreeCommandBuffers(_cmdPool, 1, &cb);
    }

    public void Init(SceneModel scene, byte[] spirv)
    {
        _scene = scene;
        _sorter = new TranslucentSorter(scene.Translucent);
        Array.Copy(scene.Colors, 0, _ublock, 24, Math.Min(scene.Colors.Length, 128));

        var cpi = new VkCommandPoolCreateInfo { flags = VkCommandPoolCreateFlags.ResetCommandBuffer, queueFamilyIndex = QueueFamily };
        VkCommandPool pool;
        Check(Api.vkCreateCommandPool(&cpi, null, &pool), "vkCreateCommandPool");
        _cmdPool = pool;

        fixed (byte* code = spirv)
        {
            var smi = new VkShaderModuleCreateInfo { codeSize = (nuint)spirv.Length, pCode = (uint*)code };
            VkShaderModule sm;
            Check(Api.vkCreateShaderModule(&smi, null, &sm), "vkCreateShaderModule");
            _module = sm;
        }

        _rpColor = RenderPass(ColorFormat);
        _rpPick = RenderPass(VkFormat.R32Uint);

        var binding = new VkDescriptorSetLayoutBinding { binding = 0, descriptorType = VkDescriptorType.UniformBufferDynamic, descriptorCount = 1, stageFlags = VkShaderStageFlags.Vertex | VkShaderStageFlags.Fragment };
        var dsl = new VkDescriptorSetLayoutCreateInfo { bindingCount = 1, pBindings = &binding };
        VkDescriptorSetLayout setLayout;
        Check(Api.vkCreateDescriptorSetLayout(&dsl, null, &setLayout), "vkCreateDescriptorSetLayout");
        _setLayout = setLayout;
        var pli = new VkPipelineLayoutCreateInfo { setLayoutCount = 1, pSetLayouts = &setLayout };
        VkPipelineLayout layout;
        Check(Api.vkCreatePipelineLayout(&pli, null, &layout), "vkCreatePipelineLayout");
        _layout = layout;

        _pOpaque = Pipeline(_rpColor, "fs_color"u8, blend: false, depthWrite: true);
        _pTrans = Pipeline(_rpColor, "fs_color"u8, blend: true, depthWrite: false);
        _pPick = Pipeline(_rpPick, "fs_pick"u8, blend: false, depthWrite: true);

        fixed (byte* p = scene.Vertices) _vb = Upload(p, scene.Vertices.Length, VkBufferUsageFlags.VertexBuffer);
        fixed (uint* p = scene.Indices) _ib = Upload(p, scene.Indices.Length * 4, VkBufferUsageFlags.IndexBuffer);

        // uniforms: per frame slot, two passes (pick, colour) — host-coherent, mapped once
        _ub = NewBuffer((ulong)(Ring * 2 * UniformStride), VkBufferUsageFlags.UniformBuffer, VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent);
        void* um;
        Check(Api.vkMapMemory(_ub.Mem, 0, VK_WHOLE_SIZE, 0, &um), "vkMapMemory");
        _uMapped = (byte*)um;
        var ps = new VkDescriptorPoolSize { type = VkDescriptorType.UniformBufferDynamic, descriptorCount = 1 };
        var dpi = new VkDescriptorPoolCreateInfo { maxSets = 1, poolSizeCount = 1, pPoolSizes = &ps };
        VkDescriptorPool dp;
        Check(Api.vkCreateDescriptorPool(&dpi, null, &dp), "vkCreateDescriptorPool");
        _pool = dp;
        var dai = new VkDescriptorSetAllocateInfo { descriptorPool = dp, descriptorSetCount = 1, pSetLayouts = &setLayout };
        VkDescriptorSet set;
        Check(Api.vkAllocateDescriptorSets(&dai, &set), "vkAllocateDescriptorSets");
        _set = set;
        var dbi = new VkDescriptorBufferInfo { buffer = _ub.Buf, offset = 0, range = UniformBytes };
        var w = new VkWriteDescriptorSet { dstSet = set, dstBinding = 0, descriptorCount = 1, descriptorType = VkDescriptorType.UniformBufferDynamic, pBufferInfo = &dbi };
        Api.vkUpdateDescriptorSets(1, &w, 0, null);

        var cai = new VkCommandBufferAllocateInfo { commandPool = _cmdPool, level = VkCommandBufferLevel.Primary, commandBufferCount = Ring };
        fixed (VkCommandBuffer* c = _cmd) Check(Api.vkAllocateCommandBuffers(&cai, c), "vkAllocateCommandBuffers");
        for (int i = 0; i < Ring; i++)
        {
            var fci = new VkFenceCreateInfo();
            VkFence f;
            Check(Api.vkCreateFence(&fci, null, &f), "vkCreateFence");
            _fence[i] = f;
            _pickBuf[i] = NewBuffer(4, VkBufferUsageFlags.TransferDst, VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent);
            void* pm;
            Check(Api.vkMapMemory(_pickBuf[i].Mem, 0, VK_WHOLE_SIZE, 0, &pm), "vkMapMemory");
            _pickMapped[i] = (uint*)pm;
        }

        (_pickImg, _pickImgMem, _pickView) = NewImage(1, 1, VkFormat.R32Uint, VkImageUsageFlags.ColorAttachment | VkImageUsageFlags.TransferSrc, VkImageAspectFlags.Color, false, out _);
        (_pickDepthImg, _pickDepthMem, _pickDepthView) = NewImage(1, 1, DepthFormat, VkImageUsageFlags.DepthStencilAttachment, VkImageAspectFlags.Depth, false, out _);
        _pickFb = Framebuffer(_rpPick, _pickView, _pickDepthView, 1, 1);
    }

    VkRenderPass RenderPass(VkFormat color)
    {
        var att = stackalloc VkAttachmentDescription[2];
        att[0] = new VkAttachmentDescription
        {
            format = color, samples = VkSampleCountFlags.Count1, loadOp = VkAttachmentLoadOp.Clear, storeOp = VkAttachmentStoreOp.Store,
            stencilLoadOp = VkAttachmentLoadOp.DontCare, stencilStoreOp = VkAttachmentStoreOp.DontCare,
            initialLayout = VkImageLayout.Undefined, finalLayout = VkImageLayout.TransferSrcOptimal,
        };
        att[1] = new VkAttachmentDescription
        {
            format = DepthFormat, samples = VkSampleCountFlags.Count1, loadOp = VkAttachmentLoadOp.Clear, storeOp = VkAttachmentStoreOp.DontCare,
            stencilLoadOp = VkAttachmentLoadOp.DontCare, stencilStoreOp = VkAttachmentStoreOp.DontCare,
            initialLayout = VkImageLayout.Undefined, finalLayout = VkImageLayout.DepthStencilAttachmentOptimal,
        };
        var cref = new VkAttachmentReference { attachment = 0, layout = VkImageLayout.ColorAttachmentOptimal };
        var dref = new VkAttachmentReference { attachment = 1, layout = VkImageLayout.DepthStencilAttachmentOptimal };
        var sub = new VkSubpassDescription { pipelineBindPoint = VkPipelineBindPoint.Graphics, colorAttachmentCount = 1, pColorAttachments = &cref, pDepthStencilAttachment = &dref };
        var deps = stackalloc VkSubpassDependency[2];
        // in: the previous reader (the compositor's copy, or our own readback) is done before we clear
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
        var rpi = new VkRenderPassCreateInfo { attachmentCount = 2, pAttachments = att, subpassCount = 1, pSubpasses = &sub, dependencyCount = 2, pDependencies = deps };
        VkRenderPass rp;
        Check(Api.vkCreateRenderPass(&rpi, null, &rp), "vkCreateRenderPass");
        return rp;
    }

    VkPipeline Pipeline(VkRenderPass rp, ReadOnlySpan<byte> fragmentEntry, bool blend, bool depthWrite)
    {
        fixed (byte* vsName = "vs"u8)
        fixed (byte* fsName = fragmentEntry)
        {
            var stages = stackalloc VkPipelineShaderStageCreateInfo[2];
            stages[0] = new VkPipelineShaderStageCreateInfo { stage = VkShaderStageFlags.Vertex, module = _module, pName = vsName };
            stages[1] = new VkPipelineShaderStageCreateInfo { stage = VkShaderStageFlags.Fragment, module = _module, pName = fsName };
            var vbd = new VkVertexInputBindingDescription { binding = 0, stride = SceneModel.VertexStride, inputRate = VkVertexInputRate.Vertex };
            var attrs = stackalloc VkVertexInputAttributeDescription[2];
            attrs[0] = new VkVertexInputAttributeDescription { location = 0, binding = 0, format = VkFormat.R32G32B32Sfloat, offset = 0 };
            attrs[1] = new VkVertexInputAttributeDescription { location = 1, binding = 0, format = VkFormat.R32Uint, offset = 12 };
            var vin = new VkPipelineVertexInputStateCreateInfo { vertexBindingDescriptionCount = 1, pVertexBindingDescriptions = &vbd, vertexAttributeDescriptionCount = 2, pVertexAttributeDescriptions = attrs };
            var ia = new VkPipelineInputAssemblyStateCreateInfo { topology = VkPrimitiveTopology.TriangleList };
            var vp = new VkPipelineViewportStateCreateInfo { viewportCount = 1, scissorCount = 1 };
            var rs = new VkPipelineRasterizationStateCreateInfo { polygonMode = VkPolygonMode.Fill, cullMode = VkCullModeFlags.None, frontFace = VkFrontFace.CounterClockwise, lineWidth = 1f };
            var ms = new VkPipelineMultisampleStateCreateInfo { rasterizationSamples = VkSampleCountFlags.Count1 };
            var ds = new VkPipelineDepthStencilStateCreateInfo { depthTestEnable = true, depthWriteEnable = depthWrite, depthCompareOp = VkCompareOp.LessOrEqual };
            var cba = new VkPipelineColorBlendAttachmentState
            {
                blendEnable = blend,
                srcColorBlendFactor = VkBlendFactor.SrcAlpha, dstColorBlendFactor = VkBlendFactor.OneMinusSrcAlpha, colorBlendOp = VkBlendOp.Add,
                // the shared image's alpha stays 1 (findings §5.1)
                srcAlphaBlendFactor = VkBlendFactor.One, dstAlphaBlendFactor = VkBlendFactor.OneMinusSrcAlpha, alphaBlendOp = VkBlendOp.Add,
                colorWriteMask = VkColorComponentFlags.All,
            };
            var cb = new VkPipelineColorBlendStateCreateInfo { attachmentCount = 1, pAttachments = &cba };
            var dyn = stackalloc VkDynamicState[2] { VkDynamicState.Viewport, VkDynamicState.Scissor };
            var dy = new VkPipelineDynamicStateCreateInfo { dynamicStateCount = 2, pDynamicStates = dyn };
            var gpi = new VkGraphicsPipelineCreateInfo
            {
                stageCount = 2, pStages = stages, pVertexInputState = &vin, pInputAssemblyState = &ia, pViewportState = &vp,
                pRasterizationState = &rs, pMultisampleState = &ms, pDepthStencilState = &ds, pColorBlendState = &cb, pDynamicState = &dy,
                layout = _layout, renderPass = rp, subpass = 0,
            };
            VkPipeline p;
            Check(Api.vkCreateGraphicsPipelines(VkPipelineCache.Null, 1, &gpi, null, &p), "vkCreateGraphicsPipelines");
            return p;
        }
    }

    (VkImage, VkDeviceMemory, VkImageView) NewImage(int w, int h, VkFormat fmt, VkImageUsageFlags usage, VkImageAspectFlags aspect, bool export, out ulong memSize)
    {
        var emi = new VkExternalMemoryImageCreateInfo { handleTypes = VkExternalMemoryHandleTypeFlags.OpaqueFD };
        var ici = new VkImageCreateInfo
        {
            pNext = export ? &emi : null,
            flags = export ? VkImageCreateFlags.MutableFormat : VkImageCreateFlags.None,
            imageType = VkImageType.Image2D, format = fmt, extent = new VkExtent3D { width = (uint)w, height = (uint)h, depth = 1 },
            mipLevels = 1, arrayLayers = 1, samples = VkSampleCountFlags.Count1, tiling = VkImageTiling.Optimal,
            usage = usage, sharingMode = VkSharingMode.Exclusive, initialLayout = VkImageLayout.Undefined,
        };
        VkImage img;
        Check(Api.vkCreateImage(&ici, null, &img), "vkCreateImage");
        VkMemoryRequirements req;
        Api.vkGetImageMemoryRequirements(img, &req);
        var ded = new VkMemoryDedicatedAllocateInfo { image = img };
        var exp = new VkExportMemoryAllocateInfo { handleTypes = VkExternalMemoryHandleTypeFlags.OpaqueFD, pNext = &ded };
        var mai = new VkMemoryAllocateInfo { pNext = export ? &exp : null, allocationSize = req.size, memoryTypeIndex = MemoryType(req.memoryTypeBits, VkMemoryPropertyFlags.DeviceLocal) };
        VkDeviceMemory m;
        Check(Api.vkAllocateMemory(&mai, null, &m), "vkAllocateMemory");
        Check(Api.vkBindImageMemory(img, m, 0), "vkBindImageMemory");
        var vci = new VkImageViewCreateInfo
        {
            image = img, viewType = VkImageViewType.Image2D, format = fmt,
            subresourceRange = new VkImageSubresourceRange { aspectMask = aspect, levelCount = 1, layerCount = 1 },
        };
        VkImageView v;
        Check(Api.vkCreateImageView(&vci, null, &v), "vkCreateImageView");
        memSize = req.size;
        return (img, m, v);
    }

    VkFramebuffer Framebuffer(VkRenderPass rp, VkImageView color, VkImageView depth, int w, int h)
    {
        var views = stackalloc VkImageView[2] { color, depth };
        var fci = new VkFramebufferCreateInfo { renderPass = rp, attachmentCount = 2, pAttachments = views, width = (uint)w, height = (uint)h, layers = 1 };
        VkFramebuffer fb;
        Check(Api.vkCreateFramebuffer(&fci, null, &fb), "vkCreateFramebuffer");
        return fb;
    }

    public VulkanTarget NewTarget(int w, int h, bool exportable)
    {
        if (exportable && !CanExport) throw new InvalidOperationException("this Vulkan device cannot export memory as a POSIX fd (VK_KHR_external_memory_fd / VK_KHR_external_semaphore_fd missing)");
        var t = new VulkanTarget { Width = w, Height = h, Exportable = exportable };
        (t.Image, t.Memory, t.View) = NewImage(w, h, ColorFormat, TargetUsage, VkImageAspectFlags.Color, exportable, out t.MemorySize);
        (t.Depth, t.DepthMemory, t.DepthView) = NewImage(w, h, DepthFormat, VkImageUsageFlags.DepthStencilAttachment, VkImageAspectFlags.Depth, false, out _);
        t.Framebuffer = Framebuffer(_rpColor, t.View, t.DepthView, w, h);
        return t;
    }

    public void DestroyTarget(VulkanTarget t)
    {
        Api.vkDeviceWaitIdle();
        Api.vkDestroyFramebuffer(t.Framebuffer, null);
        Api.vkDestroyImageView(t.View, null); Api.vkDestroyImage(t.Image, null); Api.vkFreeMemory(t.Memory, null);
        Api.vkDestroyImageView(t.DepthView, null); Api.vkDestroyImage(t.Depth, null); Api.vkFreeMemory(t.DepthMemory, null);
    }

    /// <summary>A NEW fd for the target's memory each call — the importer takes ownership of it.</summary>
    public int ExportMemoryFd(VulkanTarget t)
    {
        var gi = new VkMemoryGetFdInfoKHR { memory = t.Memory, handleType = VkExternalMemoryHandleTypeFlags.OpaqueFD };
        int fd;
        Check(Api.vkGetMemoryFdKHR(&gi, &fd), "vkGetMemoryFdKHR");
        if (fd < 0) throw new InvalidOperationException("vkGetMemoryFdKHR returned no fd");
        return fd;
    }

    public VkSemaphore NewExportableSemaphore()
    {
        var esi = new VkExportSemaphoreCreateInfo { handleTypes = VkExternalSemaphoreHandleTypeFlags.OpaqueFD };
        var sci = new VkSemaphoreCreateInfo { pNext = &esi };
        VkSemaphore s;
        Check(Api.vkCreateSemaphore(&sci, null, &s), "vkCreateSemaphore");
        return s;
    }

    public int ExportSemaphoreFd(VkSemaphore s)
    {
        var gi = new VkSemaphoreGetFdInfoKHR { semaphore = s, handleType = VkExternalSemaphoreHandleTypeFlags.OpaqueFD };
        int fd;
        Check(Api.vkGetSemaphoreFdKHR(&gi, &fd), "vkGetSemaphoreFdKHR");
        if (fd < 0) throw new InvalidOperationException("vkGetSemaphoreFdKHR returned no fd");
        return fd;
    }

    /// <summary>
    /// Record and submit one frame into <paramref name="target"/>. <paramref name="wait"/> (optional) is the
    /// compositor's "released" semaphore for this image — the colour pass waits for it; <paramref name="signal"/>
    /// (optional) is "ready", signalled when the image is in TRANSFER_SRC_OPTIMAL and complete. Blocks only for
    /// the frame submitted three frames ago, never for this one.
    /// </summary>
    public void Render(VulkanTarget target, in FrameInput input, VkSemaphore wait = default, VkSemaphore signal = default)
    {
        int f = (int)(_frame % Ring);
        CollectPicks(block: false);
        if (_submitted[f])
        {
            var fence = _fence[f];
            Check(Api.vkWaitForFences(1, &fence, true, ulong.MaxValue), "vkWaitForFences");
            ReadPick(f);
            Check(Api.vkResetFences(1, &fence), "vkResetFences");
            _submitted[f] = false;
        }
        var cb = _cmd[f];
        Api.vkResetCommandBuffer(cb, 0);
        var bi = new VkCommandBufferBeginInfo { flags = VkCommandBufferUsageFlags.OneTimeSubmit };
        Check(Api.vkBeginCommandBuffer(cb, &bi), "vkBeginCommandBuffer");
        int draws = 0;
        var clears = stackalloc VkClearValue[2];
        clears[1] = new VkClearValue(1f, 0u);
        VkBuffer vb = _vb.Buf;
        ulong zero = 0;
        var set = _set;
        float* u = (float*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref _ublock[0]);

        if (input.PickX >= 0 && input.PickY >= 0 && !_pickPending[f])
        {
            FillUniforms(u, input, pick: true);
            uint off = (uint)((f * 2) * UniformStride);
            Buffer.MemoryCopy(u, _uMapped + off, UniformBytes, UniformBytes);
            _counters.CountUniform(UniformBytes);
            clears[0] = new VkClearValue(new VkClearColorValue(0u, 0u, 0u, 0u));
            var rbi = new VkRenderPassBeginInfo { renderPass = _rpPick, framebuffer = _pickFb, renderArea = new VkRect2D(0, 0, 1, 1), clearValueCount = 2, pClearValues = clears };
            Api.vkCmdBeginRenderPass(cb, &rbi, VkSubpassContents.Inline);
            var view = new VkViewport { x = 0, y = 0, width = 1, height = 1, minDepth = 0, maxDepth = 1 };
            var sc = new VkRect2D(0, 0, 1, 1);
            Api.vkCmdSetViewport(cb, 0, 1, &view);
            Api.vkCmdSetScissor(cb, 0, 1, &sc);
            Api.vkCmdBindPipeline(cb, VkPipelineBindPoint.Graphics, _pPick);
            Api.vkCmdBindDescriptorSets(cb, VkPipelineBindPoint.Graphics, _layout, 0, 1, &set, 1, &off);
            Api.vkCmdBindVertexBuffers(cb, 0, 1, &vb, &zero);
            Api.vkCmdBindIndexBuffer(cb, _ib.Buf, 0, VkIndexType.Uint32);
            Api.vkCmdDrawIndexed(cb, (uint)_scene.OpaqueIndexCount, 1, 0, 0, 0); draws++;
            Api.vkCmdEndRenderPass(cb);
            var region = new VkBufferImageCopy
            {
                imageSubresource = new VkImageSubresourceLayers { aspectMask = VkImageAspectFlags.Color, layerCount = 1 },
                imageExtent = new VkExtent3D { width = 1, height = 1, depth = 1 },
            };
            Api.vkCmdCopyImageToBuffer(cb, _pickImg, VkImageLayout.TransferSrcOptimal, _pickBuf[f].Buf, 1, &region);
            var hb = new VkBufferMemoryBarrier
            {
                srcAccessMask = VkAccessFlags.TransferWrite, dstAccessMask = VkAccessFlags.HostRead,
                srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED, dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
                buffer = _pickBuf[f].Buf, offset = 0, size = VK_WHOLE_SIZE,
            };
            Api.vkCmdPipelineBarrier(cb, VkPipelineStageFlags.Transfer, VkPipelineStageFlags.Host, 0, 0, null, 1, &hb, 0, null);
            _pickPending[f] = true;
            _pickFrame[f] = _counters.FrameIndex;
        }

        {
            FillUniforms(u, input, pick: false);
            uint off = (uint)((f * 2 + 1) * UniformStride);
            Buffer.MemoryCopy(u, _uMapped + off, UniformBytes, UniformBytes);
            _counters.CountUniform(UniformBytes);
            clears[0] = new VkClearValue(0.11f, 0.12f, 0.14f, 1f);
            var rbi = new VkRenderPassBeginInfo { renderPass = _rpColor, framebuffer = target.Framebuffer, renderArea = new VkRect2D(0, 0, (uint)target.Width, (uint)target.Height), clearValueCount = 2, pClearValues = clears };
            Api.vkCmdBeginRenderPass(cb, &rbi, VkSubpassContents.Inline);
            var view = new VkViewport { x = 0, y = 0, width = target.Width, height = target.Height, minDepth = 0, maxDepth = 1 };
            var sc = new VkRect2D(0, 0, (uint)target.Width, (uint)target.Height);
            Api.vkCmdSetViewport(cb, 0, 1, &view);
            Api.vkCmdSetScissor(cb, 0, 1, &sc);
            Api.vkCmdBindDescriptorSets(cb, VkPipelineBindPoint.Graphics, _layout, 0, 1, &set, 1, &off);
            Api.vkCmdBindVertexBuffers(cb, 0, 1, &vb, &zero);
            Api.vkCmdBindIndexBuffer(cb, _ib.Buf, 0, VkIndexType.Uint32);
            Api.vkCmdBindPipeline(cb, VkPipelineBindPoint.Graphics, _pOpaque);
            Api.vkCmdDrawIndexed(cb, (uint)_scene.OpaqueIndexCount, 1, 0, 0, 0); draws++;
            _sorter.Sort(input.Camera);
            Api.vkCmdBindPipeline(cb, VkPipelineBindPoint.Graphics, _pTrans);
            var tr = _scene.Translucent;
            foreach (int i in _sorter.Order) { Api.vkCmdDrawIndexed(cb, (uint)tr[i].IndexCount, 1, (uint)tr[i].FirstIndex, 0, 0); draws++; }
            Api.vkCmdEndRenderPass(cb);
        }
        Check(Api.vkEndCommandBuffer(cb), "vkEndCommandBuffer");

        var waitStage = VkPipelineStageFlags.ColorAttachmentOutput;
        var si = new VkSubmitInfo
        {
            waitSemaphoreCount = wait.Handle != 0 ? 1u : 0u, pWaitSemaphores = &wait, pWaitDstStageMask = &waitStage,
            commandBufferCount = 1, pCommandBuffers = &cb,
            signalSemaphoreCount = signal.Handle != 0 ? 1u : 0u, pSignalSemaphores = &signal,
        };
        Check(Api.vkQueueSubmit(Queue, 1, &si, _fence[f]), "vkQueueSubmit");
        _submitted[f] = true;
        _frame++;
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

    void CollectPicks(bool block)
    {
        for (int k = 1; k <= Ring; k++)
        {
            int s = (int)((_frame + k) % Ring);   // oldest first
            if (!_pickPending[s] || !_submitted[s]) continue;
            if (Api.vkGetFenceStatus(_fence[s]) != VkResult.Success) continue;
            ReadPick(s);
        }
    }

    void ReadPick(int s)
    {
        if (!_pickPending[s]) return;
        HoveredId = *_pickMapped[s];
        _pickPending[s] = false;
        _counters.PickResolved(_pickFrame[s]);
    }

    public bool PickPending { get { foreach (var b in _pickPending) if (b) return true; return false; } }

    /// <summary>Harness only: waits for the GPU, copies the target (TRANSFER_SRC_OPTIMAL) to a host
    /// buffer and returns RGBA rows.</summary>
    public byte[] ReadRgba(VulkanTarget t)
    {
        Api.vkDeviceWaitIdle();
        ulong size = (ulong)(t.Width * t.Height * 4);
        var buf = NewBuffer(size, VkBufferUsageFlags.TransferDst, VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent);
        OneShot(cb =>
        {
            var region = new VkBufferImageCopy
            {
                imageSubresource = new VkImageSubresourceLayers { aspectMask = VkImageAspectFlags.Color, layerCount = 1 },
                imageExtent = new VkExtent3D { width = (uint)t.Width, height = (uint)t.Height, depth = 1 },
            };
            Api.vkCmdCopyImageToBuffer(cb, t.Image, VkImageLayout.TransferSrcOptimal, buf.Item1, 1, &region);
        });
        void* p;
        Check(Api.vkMapMemory(buf.Item2, 0, size, 0, &p), "vkMapMemory");
        var px = new byte[size];
        Marshal.Copy((nint)p, px, 0, px.Length);
        Api.vkUnmapMemory(buf.Item2);
        Api.vkDestroyBuffer(buf.Item1, null);
        Api.vkFreeMemory(buf.Item2, null);
        return px;
    }

    public void Dispose()
    {
        if (Device.Handle == 0) return;
        Api.vkDeviceWaitIdle();
        Api.vkDestroyDevice(null);
        _vi.vkDestroyInstance(null);
    }
}
