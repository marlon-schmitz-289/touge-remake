using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;

namespace Penelope.Backends.Vulkan;

/// <summary>
///     Vulkan backend for Penelope. Requires Vulkan 1.3 (for dynamic rendering + synchronization2).
///     Construct via <see cref="Create"/>, supplying a surface factory that wraps whatever
///     windowing system the host uses (SDL, GLFW, native, ...).
/// </summary>
public sealed unsafe partial class VulkanDevice : IPenelopeDevice
{
    public delegate SurfaceKHR SurfaceFactory(Instance instance);

    internal readonly Vk Vk;
    internal readonly Instance Instance;
    internal readonly PhysicalDevice PhysicalDevice;
    internal readonly Device Device;
    internal readonly Queue GraphicsQueue;
    internal readonly uint GraphicsQueueFamily;
    internal readonly CommandPool GraphicsPool;
    internal readonly DescriptorPool DescriptorPool;

    private readonly KhrSurface _surfaceExt;
    private readonly KhrSwapchain _swapchainExt;
    private readonly SurfaceKHR _surface;
    private readonly VulkanSwapchain _swapchain;

    /// <summary>VK_EXT_debug_utils, when the loader exposes it. Null = debug labels are a no-op.</summary>
    private readonly ExtDebugUtils? _debugUtils;

    private PhysicalDeviceMemoryProperties _memProps;
    private readonly MemoryType[] _memoryTypes;
    private readonly PhysicalDeviceProperties _deviceProps;

    // Resource tables
    private readonly HandleTable<VulkanBuffer> _buffers = new("Buffer");
    private readonly HandleTable<VulkanImage> _images = new("Image");
    private readonly HandleTable<VulkanImageView> _imageViews = new("ImageView");
    private readonly HandleTable<VulkanSampler> _samplers = new("Sampler");
    private readonly Dictionary<SamplerDesc, SamplerHandle> _samplerCache = new();
    private readonly HandleTable<VulkanShader> _shaders = new("Shader");
    private readonly HandleTable<VulkanRenderPipeline> _renderPipelines = new("RenderPipeline");
    private readonly HandleTable<VulkanComputePipeline> _computePipelines = new("ComputePipeline");
    private readonly HandleTable<VulkanBindGroupLayout> _bindGroupLayouts = new("BindGroupLayout");
    private readonly Dictionary<BindGroupLayoutKey, BindGroupLayoutHandle> _bgLayoutCache = new();
    private readonly HandleTable<VulkanBindGroup> _bindGroups = new("BindGroup");
    private readonly HandleTable<VulkanRenderTarget> _renderTargets = new("RenderTarget");
    private readonly HandleTable<VulkanQueryPool> _queryPools = new("QueryPool");
    private readonly HandleTable<VulkanFence> _fences = new("Fence");

    private ulong _nextHandleId = 1;

    // Per-frame state
    private const int FramesInFlightCount = 2;
    private readonly FrameData[] _frames = new FrameData[FramesInFlightCount];
    private int _frameIndex;
    private bool _frameActive;
    private bool _submittedThisFrame; // first-submit-of-frame tracking (waits ImageAvailable once)
    private bool _needsRecreate;      // set on OutOfDate/Suboptimal acquire or present; recreate at next BeginFrame

    public BackendKind Backend => BackendKind.Vulkan;
    public int FramesInFlight => FramesInFlightCount;
    public int CurrentFrameSlot => _frameIndex;
    public AdapterInfo Adapter { get; }
    public DeviceFeatures Features { get; }
    public DeviceLimits Limits { get; }

    public int SwapchainWidth => (int)_swapchain.Extent.Width;
    public int SwapchainHeight => (int)_swapchain.Extent.Height;
    public TextureFormat SwapchainFormat => VulkanConvert.FromVk(_swapchain.Format);
    public PresentMode PresentMode => FromVkPresentMode(_swapchain.PresentMode);

    public TextureViewHandle CurrentSwapchainView =>
        new(_swapchain.ImageViewIds[_swapchain.CurrentImageIndex]);

    public double TimestampPeriodNs => _deviceProps.Limits.TimestampPeriod;

    private VulkanDevice(
        Vk vk,
        Instance instance,
        SurfaceKHR surface,
        PhysicalDevice phys,
        uint queueFamily,
        Device device,
        KhrSurface surfaceExt,
        KhrSwapchain swapchainExt,
        ExtDebugUtils? debugUtils = null)
    {
        Vk = vk;
        Instance = instance;
        _surface = surface;
        PhysicalDevice = phys;
        GraphicsQueueFamily = queueFamily;
        Device = device;
        _surfaceExt = surfaceExt;
        _swapchainExt = swapchainExt;
        _debugUtils = debugUtils;

        vk.GetDeviceQueue(device, queueFamily, 0, out GraphicsQueue);
        vk.GetPhysicalDeviceMemoryProperties(phys, out _memProps);
        vk.GetPhysicalDeviceProperties(phys, out _deviceProps);

        _memoryTypes = new MemoryType[_memProps.MemoryTypeCount];
        var localMemProps = _memProps;
        var memTypesBuf = localMemProps.MemoryTypes;
        var memTypesSpan = memTypesBuf.AsSpan();
        for (uint i = 0; i < localMemProps.MemoryTypeCount; i++)
            _memoryTypes[i] = memTypesSpan[(int)i];

        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = queueFamily,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
        };
        vk.CreateCommandPool(device, &poolInfo, null, out GraphicsPool).ThrowIfError();

        // Descriptor pool — generous limits; bump or add pool rotation if exhausted.
        var poolSizes = stackalloc DescriptorPoolSize[]
        {
            new(DescriptorType.UniformBuffer, 1024),
            new(DescriptorType.UniformBufferDynamic, 256),
            new(DescriptorType.StorageBuffer, 512),
            new(DescriptorType.SampledImage, 2048),
            new(DescriptorType.StorageImage, 256),
            new(DescriptorType.Sampler, 512),
            new(DescriptorType.CombinedImageSampler, 512),
        };
        var dpInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 7,
            PPoolSizes = poolSizes,
            MaxSets = 4096,
            Flags = DescriptorPoolCreateFlags.FreeDescriptorSetBit,
        };
        vk.CreateDescriptorPool(device, &dpInfo, null, out DescriptorPool).ThrowIfError();

        _swapchain = new VulkanSwapchain(vk, this, swapchainExt, surfaceExt, surface);

        // Per-frame data
        for (var i = 0; i < FramesInFlightCount; i++)
            _frames[i] = CreateFrameData();

        // Populate adapter info / features / limits
        var localDevProps = _deviceProps;
        byte* pName = localDevProps.DeviceName;
        var name = Marshal.PtrToStringAnsi((nint)pName) ?? "Unknown";
        Adapter = new AdapterInfo(
            BackendKind.Vulkan,
            name,
            $"apiVersion=0x{_deviceProps.ApiVersion:X} driverVersion=0x{_deviceProps.DriverVersion:X}",
            _deviceProps.VendorID,
            _deviceProps.DeviceID,
            _deviceProps.DeviceType == PhysicalDeviceType.DiscreteGpu);

        Features = BuildFeatures();
        Limits = BuildLimits();
    }

    /// <summary>
    ///     Create a Vulkan device. <paramref name="surfaceFactory"/> is invoked once after the
    ///     instance is created and must return a surface for the target window.
    /// </summary>
    public static VulkanDevice Create(
        SurfaceFactory surfaceFactory,
        in DeviceDesc deviceDesc,
        in SwapchainDesc swapchainDesc,
        IEnumerable<string>? extraInstanceExtensions = null)
    {
        var vk = Vk.GetApi();

        // Instance
        var appName = SilkMarshal.StringToPtr("Penelope");
        var engineName = SilkMarshal.StringToPtr("Penelope");
        var appInfo = new ApplicationInfo
        {
            SType = StructureType.ApplicationInfo,
            PApplicationName = (byte*)appName,
            ApplicationVersion = Vk.MakeVersion(1, 0, 0),
            PEngineName = (byte*)engineName,
            EngineVersion = Vk.MakeVersion(1, 0, 0),
            ApiVersion = Vk.Version13,
        };

        var instExts = new List<string>
        {
            KhrSurface.ExtensionName,
        };
        if (extraInstanceExtensions != null) instExts.AddRange(extraInstanceExtensions);
        var isMac = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        if (isMac) instExts.Add("VK_KHR_portability_enumeration");

        // VK_EXT_debug_utils is optional — request it only if the loader actually exposes it, so a
        // host without it (older loader / headless) still creates an instance. When present it lets
        // SetDebugLabel name objects for RenderDoc / NSight captures.
        var hasDebugUtils = InstanceExtensionAvailable(vk, ExtDebugUtils.ExtensionName);
        if (hasDebugUtils) instExts.Add(ExtDebugUtils.ExtensionName);

        var layers = new List<string>();
        if (deviceDesc.EnableValidation)
        {
            const string validation = "VK_LAYER_KHRONOS_validation";
            uint layerCount = 0;
            vk.EnumerateInstanceLayerProperties(&layerCount, null);
            var available = stackalloc LayerProperties[(int)layerCount];
            vk.EnumerateInstanceLayerProperties(&layerCount, available);
            var found = false;
            for (uint i = 0; i < layerCount; i++)
            {
                if (Marshal.PtrToStringAnsi((nint)(available + i)->LayerName) == validation)
                {
                    found = true;
                    break;
                }
            }
            if (found) layers.Add(validation);
            else Console.Error.WriteLine($"[Vulkan] {validation} not available; continuing without it.");
        }

        var pExts = (byte**)SilkMarshal.StringArrayToPtr(instExts);
        var pLayers = (byte**)SilkMarshal.StringArrayToPtr(layers);

        var instInfo = new InstanceCreateInfo
        {
            SType = StructureType.InstanceCreateInfo,
            PApplicationInfo = &appInfo,
            EnabledExtensionCount = (uint)instExts.Count,
            PpEnabledExtensionNames = pExts,
            EnabledLayerCount = (uint)layers.Count,
            PpEnabledLayerNames = pLayers,
            Flags = isMac ? InstanceCreateFlags.EnumeratePortabilityBitKhr : 0,
        };

        vk.CreateInstance(&instInfo, null, out var instance).ThrowIfError();

        SilkMarshal.Free(appName);
        SilkMarshal.Free(engineName);
        SilkMarshal.Free((nint)pExts);
        SilkMarshal.Free((nint)pLayers);

        // Extensions
        if (!vk.TryGetInstanceExtension<KhrSurface>(instance, out var surfaceExt))
            throw new Exception("VK_KHR_surface unavailable.");

        // Surface
        var surface = surfaceFactory(instance);

        // Pick physical device
        var phys = PickPhysicalDevice(vk, instance, surface, surfaceExt, deviceDesc, out var queueFamily);

        // Logical device
        var queuePriority = 1f;
        var queueInfo = new DeviceQueueCreateInfo
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = queueFamily,
            QueueCount = 1,
            PQueuePriorities = &queuePriority,
        };

        var deviceExts = new List<string>
        {
            KhrSwapchain.ExtensionName,
        };
        if (isMac) deviceExts.Add("VK_KHR_portability_subset");
        var pDeviceExts = (byte**)SilkMarshal.StringArrayToPtr(deviceExts);

        // Enable Vulkan 1.3 features: dynamic rendering + sync2
        var features13 = new PhysicalDeviceVulkan13Features
        {
            SType = StructureType.PhysicalDeviceVulkan13Features,
            DynamicRendering = true,
            Synchronization2 = true,
            // Needed for shaders that compile `discard` / `demote` to SPIR-V
            // OpDemoteToHelperInvocation (default with recent glslc).
            ShaderDemoteToHelperInvocation = true,
        };
        var features12 = new PhysicalDeviceVulkan12Features
        {
            SType = StructureType.PhysicalDeviceVulkan12Features,
            PNext = &features13,
            DescriptorIndexing = true,
            TimelineSemaphore = true,
        };
        vk.GetPhysicalDeviceFeatures(phys, out var availableFeatures);
        var baseFeatures = new PhysicalDeviceFeatures
        {
            SamplerAnisotropy = true,
            FillModeNonSolid = availableFeatures.FillModeNonSolid,
            WideLines = availableFeatures.WideLines,
            MultiDrawIndirect = availableFeatures.MultiDrawIndirect,
        };

        var deviceInfo = new DeviceCreateInfo
        {
            SType = StructureType.DeviceCreateInfo,
            PNext = &features12,
            QueueCreateInfoCount = 1,
            PQueueCreateInfos = &queueInfo,
            EnabledExtensionCount = (uint)deviceExts.Count,
            PpEnabledExtensionNames = pDeviceExts,
            PEnabledFeatures = &baseFeatures,
        };

        vk.CreateDevice(phys, &deviceInfo, null, out var device).ThrowIfError();
        SilkMarshal.Free((nint)pDeviceExts);

        if (!vk.TryGetDeviceExtension<KhrSwapchain>(instance, device, out var swapchainExt))
            throw new Exception("VK_KHR_swapchain unavailable.");

        ExtDebugUtils? debugUtils = null;
        if (hasDebugUtils && vk.TryGetInstanceExtension<ExtDebugUtils>(instance, out var du))
            debugUtils = du;

        var dev = new VulkanDevice(
            vk, instance, surface, phys, queueFamily, device,
            surfaceExt, swapchainExt, debugUtils);

        dev._swapchain.Configure(swapchainDesc);
        return dev;
    }

    /// <summary>True if the given instance extension is advertised by the Vulkan loader.</summary>
    private static bool InstanceExtensionAvailable(Vk vk, string name)
    {
        uint count = 0;
        vk.EnumerateInstanceExtensionProperties((byte*)null, &count, null);
        if (count == 0) return false;
        var props = stackalloc ExtensionProperties[(int)count];
        vk.EnumerateInstanceExtensionProperties((byte*)null, &count, props);
        for (uint i = 0; i < count; i++)
            if (Marshal.PtrToStringAnsi((nint)props[i].ExtensionName) == name)
                return true;
        return false;
    }

    private static PhysicalDevice PickPhysicalDevice(
        Vk vk, Instance instance, SurfaceKHR surface, KhrSurface surfaceExt,
        in DeviceDesc desc, out uint queueFamily)
    {
        uint count = 0;
        vk.EnumeratePhysicalDevices(instance, &count, null);
        if (count == 0) throw new Exception("No Vulkan physical devices found.");
        var devices = new PhysicalDevice[count];
        fixed (PhysicalDevice* p = devices)
            vk.EnumeratePhysicalDevices(instance, &count, p);

        PhysicalDevice best = default;
        uint bestFamily = 0;
        var bestScore = -1;

        foreach (var pd in devices)
        {
            vk.GetPhysicalDeviceProperties(pd, out var props);
            var score = props.DeviceType switch
            {
                PhysicalDeviceType.DiscreteGpu => 1000,
                PhysicalDeviceType.IntegratedGpu => 500,
                PhysicalDeviceType.VirtualGpu => 100,
                PhysicalDeviceType.Cpu => 10,
                _ => 0,
            };
            if (desc.Preference == AdapterPreference.LowPower && props.DeviceType == PhysicalDeviceType.IntegratedGpu)
                score += 2000;
            if (desc.Preference == AdapterPreference.Software && props.DeviceType == PhysicalDeviceType.Cpu)
                score += 5000;

            // Find a graphics + present queue family
            uint qCount = 0;
            vk.GetPhysicalDeviceQueueFamilyProperties(pd, &qCount, null);
            var qProps = new QueueFamilyProperties[qCount];
            fixed (QueueFamilyProperties* qp = qProps)
                vk.GetPhysicalDeviceQueueFamilyProperties(pd, &qCount, qp);

            var found = false;
            for (uint qi = 0; qi < qCount; qi++)
            {
                if ((qProps[qi].QueueFlags & QueueFlags.GraphicsBit) == 0) continue;
                surfaceExt.GetPhysicalDeviceSurfaceSupport(pd, qi, surface, out var presentSupport);
                if (!presentSupport) continue;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = pd;
                    bestFamily = qi;
                }
                found = true;
                break;
            }
            if (!found) continue;
        }

        if (bestScore < 0) throw new Exception("No suitable Vulkan device found.");
        queueFamily = bestFamily;
        return best;
    }

    private DeviceFeatures BuildFeatures()
    {
        DeviceFeatures f = DeviceFeatures.DepthBiasClamp | DeviceFeatures.ComputeShaders | DeviceFeatures.AnisotropicFiltering;
        vk_ReadFeaturesInto(ref f);
        return f;
    }

    private void vk_ReadFeaturesInto(ref DeviceFeatures f)
    {
        Vk.GetPhysicalDeviceFeatures(PhysicalDevice, out var feats);
        if (feats.TextureCompressionBC) f |= DeviceFeatures.TextureCompressionBC;
        if (feats.TextureCompressionEtc2) f |= DeviceFeatures.TextureCompressionETC2;
        if (feats.TextureCompressionAstcLdr) f |= DeviceFeatures.TextureCompressionASTC;
        if (feats.ShaderFloat64) f |= DeviceFeatures.ShaderFloat64;
        if (feats.ShaderInt64) f |= DeviceFeatures.ShaderInt64;
        if (feats.ShaderInt16) f |= DeviceFeatures.ShaderInt16;
        if (feats.MultiDrawIndirect) f |= DeviceFeatures.MultiDrawIndirect;
        if (feats.DrawIndirectFirstInstance) f |= DeviceFeatures.IndirectFirstInstance;
        if (feats.DualSrcBlend) f |= DeviceFeatures.DualSourceBlending;
        if (feats.GeometryShader) f |= DeviceFeatures.GeometryShaders;
        if (feats.TessellationShader) f |= DeviceFeatures.TessellationShaders;
        if (feats.DepthBiasClamp) f |= DeviceFeatures.DepthBiasClamp;
        if (feats.DepthClamp) f |= DeviceFeatures.DepthClamping;
    }

    private DeviceLimits BuildLimits()
    {
        var localProps = _deviceProps;
        var l = localProps.Limits;
        uint* pSize = l.MaxComputeWorkGroupSize;
        var wg0 = (int)pSize[0];
        var wg1 = (int)pSize[1];
        var wg2 = (int)pSize[2];
        return new DeviceLimits(
            MaxTextureDimension2D: (int)l.MaxImageDimension2D,
            MaxTextureDimension3D: (int)l.MaxImageDimension3D,
            MaxTextureArrayLayers: (int)l.MaxImageArrayLayers,
            MaxBindGroups: (int)l.MaxBoundDescriptorSets,
            MaxBindingsPerBindGroup: (int)l.MaxPerStageResources,
            MaxDynamicUniformBuffersPerPipeline: (int)l.MaxDescriptorSetUniformBuffersDynamic,
            MaxDynamicStorageBuffersPerPipeline: (int)l.MaxDescriptorSetStorageBuffersDynamic,
            MaxSampledTexturesPerShaderStage: (int)l.MaxPerStageDescriptorSampledImages,
            MaxSamplersPerShaderStage: (int)l.MaxPerStageDescriptorSamplers,
            MaxStorageBuffersPerShaderStage: (int)l.MaxPerStageDescriptorStorageBuffers,
            MaxStorageTexturesPerShaderStage: (int)l.MaxPerStageDescriptorStorageImages,
            MaxUniformBuffersPerShaderStage: (int)l.MaxPerStageDescriptorUniformBuffers,
            MaxUniformBufferBindingSize: (long)l.MaxUniformBufferRange,
            MaxStorageBufferBindingSize: (long)l.MaxStorageBufferRange,
            MinUniformBufferOffsetAlignment: (int)l.MinUniformBufferOffsetAlignment,
            MinStorageBufferOffsetAlignment: (int)l.MinStorageBufferOffsetAlignment,
            MaxVertexBuffers: (int)l.MaxVertexInputBindings,
            MaxVertexAttributes: (int)l.MaxVertexInputAttributes,
            MaxVertexBufferArrayStride: (int)l.MaxVertexInputBindingStride,
            MaxPushConstantsSize: (int)l.MaxPushConstantsSize,
            MaxColorAttachments: (int)l.MaxColorAttachments,
            MaxComputeWorkgroupSizeX: wg0,
            MaxComputeWorkgroupSizeY: wg1,
            MaxComputeWorkgroupSizeZ: wg2,
            MaxComputeInvocationsPerWorkgroup: (int)l.MaxComputeWorkGroupInvocations,
            MaxSamplerAnisotropy: l.MaxSamplerAnisotropy);
    }

    private static Penelope.PresentMode FromVkPresentMode(PresentModeKHR m) => m switch
    {
        PresentModeKHR.FifoKhr => Penelope.PresentMode.Fifo,
        PresentModeKHR.FifoRelaxedKhr => Penelope.PresentMode.FifoRelaxed,
        PresentModeKHR.MailboxKhr => Penelope.PresentMode.Mailbox,
        PresentModeKHR.ImmediateKhr => Penelope.PresentMode.Immediate,
        _ => Penelope.PresentMode.Fifo,
    };

    public void ConfigureSwapchain(in SwapchainDesc desc)
    {
        WaitIdle();
        _swapchain.Configure(desc);
    }

    public void ResizeSwapchain(int width, int height)
    {
        WaitIdle();
        _swapchain.Configure(new SwapchainDesc(
            width, height,
            VulkanConvert.FromVk(_swapchain.Format),
            FromVkPresentMode(_swapchain.PresentMode)));
    }

    public void SetVSync(bool vsync)
    {
        WaitIdle();
        // Mailbox instead of Fifo: still vsync-locked (no tearing) but doesn't queue frames behind
        // a full present cycle, so input-to-photon latency doesn't balloon like Fifo's frame queue
        // does. Falls back to guaranteed Fifo in VulkanSwapchain.Configure if unsupported.
        var newMode = vsync ? Penelope.PresentMode.Mailbox : Penelope.PresentMode.Immediate;
        if (FromVkPresentMode(_swapchain.PresentMode) == newMode) return;
        _swapchain.Configure(new SwapchainDesc(
            (int)_swapchain.Extent.Width,
            (int)_swapchain.Extent.Height,
            VulkanConvert.FromVk(_swapchain.Format),
            newMode));
    }

    internal ulong NewHandleId() => _nextHandleId++;

    internal ulong RegisterImage(VulkanImage img)
    {
        var id = NewHandleId();
        _images[id] = img;
        return id;
    }

    internal ulong RegisterImageView(VulkanImageView v)
    {
        var id = NewHandleId();
        _imageViews[id] = v;
        return id;
    }

    internal bool TryGetImage(ulong id, out VulkanImage img) => _images.TryGetValue(id, out img!);
    internal bool TryGetImageView(ulong id, out VulkanImageView v) => _imageViews.TryGetValue(id, out v!);
    internal void UnregisterImage(ulong id) => _images.Remove(id);
    internal void UnregisterImageView(ulong id) => _imageViews.Remove(id);

    internal uint FindMemoryType(uint typeBits, MemoryPropertyFlags needed)
    {
        for (uint i = 0; i < _memoryTypes.Length; i++)
        {
            if ((typeBits & (1u << (int)i)) == 0) continue;
            if ((_memoryTypes[i].PropertyFlags & needed) == needed) return i;
        }
        throw new Exception($"No memory type matching bits=0x{typeBits:X} flags={needed}.");
    }

    public void WaitIdle() => Vk.DeviceWaitIdle(Device);

    public void Dispose()
    {
        WaitIdle();

        // Swapchain first — it owns image views we also track in _imageViews; disposing the
        // swapchain unregisters them from our tables before the bulk loops below run.
        _swapchain.Dispose();

        foreach (var b in _buffers.Values) DestroyBufferInternal(b);
        foreach (var img in _images.Values) DestroyImageInternal(img);
        foreach (var v in _imageViews.Values) Vk.DestroyImageView(Device, v.View, null);
        foreach (var s in _samplers.Values) Vk.DestroySampler(Device, s.Sampler, null);
        foreach (var sh in _shaders.Values) DestroyShaderInternal(sh);
        foreach (var p in _renderPipelines.Values)
        {
            Vk.DestroyPipeline(Device, p.Pipeline, null);
            Vk.DestroyPipelineLayout(Device, p.Layout, null);
        }
        foreach (var p in _computePipelines.Values)
        {
            Vk.DestroyPipeline(Device, p.Pipeline, null);
            Vk.DestroyPipelineLayout(Device, p.Layout, null);
        }
        foreach (var l in _bindGroupLayouts.Values)
            Vk.DestroyDescriptorSetLayout(Device, l.Layout, null);
        foreach (var q in _queryPools.Values)
            Vk.DestroyQueryPool(Device, q.Pool, null);
        foreach (var f in _fences.Values)
            Vk.DestroyFence(Device, f.Fence, null);

        for (var i = 0; i < FramesInFlightCount; i++)
            DestroyFrameData(_frames[i]);

        Vk.DestroyDescriptorPool(Device, DescriptorPool, null);
        Vk.DestroyCommandPool(Device, GraphicsPool, null);
        Vk.DestroyDevice(Device, null);
        _surfaceExt.DestroySurface(Instance, _surface, null);
        Vk.DestroyInstance(Instance, null);
        Vk.Dispose();
    }

    public void SetDebugLabel<T>(T handle, string label) where T : struct
    {
        if (_debugUtils == null) return;

        // Resolve the Penelope handle to its underlying Vulkan object + type. Boxing here is fine:
        // SetDebugLabel is a diagnostic call made at resource-creation time, not per frame.
        var (objType, raw) = ResolveVkObject(handle);
        if (raw == 0) return;

        var pName = SilkMarshal.StringToPtr(label);
        var info = new DebugUtilsObjectNameInfoEXT
        {
            SType = StructureType.DebugUtilsObjectNameInfoExt,
            ObjectType = objType,
            ObjectHandle = raw,
            PObjectName = (byte*)pName,
        };
        _debugUtils.SetDebugUtilsObjectName(Device, &info);
        SilkMarshal.Free(pName);
    }

    private (ObjectType type, ulong raw) ResolveVkObject<T>(T handle) where T : struct => handle switch
    {
        BufferHandle h when _buffers.TryGetValue(h.Id, out var b) => (ObjectType.Buffer, b.Buffer.Handle),
        TextureHandle h when _images.TryGetValue(h.Id, out var i) => (ObjectType.Image, i.Image.Handle),
        TextureViewHandle h when _imageViews.TryGetValue(h.Id, out var v) => (ObjectType.ImageView, v.View.Handle),
        SamplerHandle h when _samplers.TryGetValue(h.Id, out var s) => (ObjectType.Sampler, s.Sampler.Handle),
        RenderPipelineHandle h when _renderPipelines.TryGetValue(h.Id, out var p) => (ObjectType.Pipeline, p.Pipeline.Handle),
        ComputePipelineHandle h when _computePipelines.TryGetValue(h.Id, out var p) => (ObjectType.Pipeline, p.Pipeline.Handle),
        BindGroupHandle h when _bindGroups.TryGetValue(h.Id, out var g) => (ObjectType.DescriptorSet, g.Set.Handle),
        BindGroupLayoutHandle h when _bindGroupLayouts.TryGetValue(h.Id, out var l) => (ObjectType.DescriptorSetLayout, l.Layout.Handle),
        QueryPoolHandle h when _queryPools.TryGetValue(h.Id, out var q) => (ObjectType.QueryPool, q.Pool.Handle),
        _ => (ObjectType.Unknown, 0ul),
    };
}
