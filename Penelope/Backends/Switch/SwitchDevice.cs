namespace Penelope.Backends.Switch;

/// <summary>
///     Nintendo Switch / Switch 2 backend for Penelope, sitting on top of NVN2 (Nintendo's
///     proprietary low-level graphics API; SPIR-V-consuming, derived from Vulkan concepts).
///     <para><b>Status:</b> skeleton. Mirrors the structure of the Vulkan / OpenGL / Metal
///     backends so the implementation slot for each method is obvious; every method throws
///     <see cref="NotImplementedException"/> with a comment pointing at the NVN2 call needed.
///     Builds on every platform (no Nintendo SDK references in the managed code yet) but only
///     becomes useful once linked against the real SDK from a Switch dev kit.</para>
///
///     <para>Construction needs a <see cref="SwitchContextProvider"/> the host fills in with
///     an <c>NVNwindow</c> handle wired to the system's compositor (typically created via
///     <c>nn::vi::CreateLayer</c> + <c>nvnWindowBuilderSetNativeWindow</c>). The host owns the
///     window; Penelope owns the device + queue + command-buffer lifecycle from there on.</para>
/// </summary>
public sealed unsafe partial class SwitchDevice : IPenelopeDevice
{
    /// <summary>
    ///     Bridge between the host's NWindow and Penelope. <see cref="NvnWindow"/> is the
    ///     <c>NVNwindow*</c> the host built from the system's <c>nn::vi</c> layer;
    ///     <see cref="GetDrawableSize"/> reports the current surface size each frame.
    /// </summary>
    public sealed class SwitchContextProvider
    {
        public required nint NvnWindow { get; init; }
        public required Func<(int width, int height)> GetDrawableSize { get; init; }
    }

    internal nint NvnDevice;        // NVNdevice*
    internal nint NvnQueue;         // NVNqueue*
    internal nint NvnWindow;        // NVNwindow* (owned by host, not us)

    private readonly SwitchContextProvider _context;
    private readonly HandleTable<SwitchBuffer> _buffers = new("Buffer");
    private readonly HandleTable<SwitchTexture> _textures = new("Texture");
    private readonly HandleTable<SwitchTextureView> _textureViews = new("TextureView");
    private readonly HandleTable<SwitchSampler> _samplers = new("Sampler");
    private readonly HandleTable<SwitchShader> _shaders = new("Shader");
    private readonly HandleTable<SwitchRenderPipeline> _renderPipelines = new("RenderPipeline");
    private readonly HandleTable<SwitchComputePipeline> _computePipelines = new("ComputePipeline");
    private readonly HandleTable<SwitchBindGroupLayout> _bindGroupLayouts = new("BindGroupLayout");
    private readonly HandleTable<SwitchBindGroup> _bindGroups = new("BindGroup");
    private readonly HandleTable<SwitchRenderTarget> _renderTargets = new("RenderTarget");
    private readonly HandleTable<SwitchQueryPool> _queryPools = new("QueryPool");
    private readonly HandleTable<SwitchFence> _fences = new("Fence");
    private ulong _nextHandleId = 1;

    private TextureFormat _swapchainFormat;
    private PresentMode _presentMode;
    private int _swapchainWidth;
    private int _swapchainHeight;
    private ulong _swapchainTextureId;
    private ulong _swapchainViewId;

    public BackendKind Backend => BackendKind.Null; // TODO: extend BackendKind enum with Switch when shipping
    // NVN typically runs with double-buffered swap; pick 2 to match. Real impl will wait on the
    // slot's prior NVNsync at BeginFrame the same way Vulkan does.
    public int FramesInFlight => 2;
    public int CurrentFrameSlot => 0;
    public AdapterInfo Adapter { get; private set; }
    public DeviceFeatures Features { get; private set; }
    public DeviceLimits Limits { get; private set; }
    public int SwapchainWidth => _swapchainWidth;
    public int SwapchainHeight => _swapchainHeight;
    public TextureFormat SwapchainFormat => _swapchainFormat;
    public PresentMode PresentMode => _presentMode;
    public TextureViewHandle CurrentSwapchainView => new(_swapchainViewId);
    public double TimestampPeriodNs => 1.0; // NVN reports ns directly via NVNcounterData.

    private SwitchDevice(SwitchContextProvider context)
    {
        _context = context;
        NvnWindow = context.NvnWindow;
        // TODO: NVN init sequence
        //   1. nvnBootstrapLoader → load libnvn.dll equivalent
        //   2. nvnDeviceBuilderSetFlags, nvnDeviceInitialize → NvnDevice
        //   3. nvnQueueBuilderSetDevice + nvnQueueInitialize → NvnQueue
        //   4. Acquire memory pools (NVNmemoryPool) for buffers / textures / shaders
    }

    /// <summary>
    ///     Create a Switch device. Caller has already set up the NWindow via the system layer.
    ///     Throws on non-Switch platforms (no NVN runtime to load).
    /// </summary>
    public static SwitchDevice Create(
        SwitchContextProvider context,
        in DeviceDesc deviceDesc,
        in SwapchainDesc swapchainDesc)
    {
        var dev = new SwitchDevice(context);
        dev._swapchainFormat = swapchainDesc.Format;
        dev._presentMode = swapchainDesc.PresentMode;
        var (sw, sh) = context.GetDrawableSize();
        dev._swapchainWidth = sw > 0 ? sw : swapchainDesc.Width;
        dev._swapchainHeight = sh > 0 ? sh : swapchainDesc.Height;

        // TODO: configure NVNwindow swapchain (typically 2-3 textures via nvnWindowBuilder*).
        // For each swapchain texture, register it as a Penelope texture handle so the engine
        // can use CurrentSwapchainView like any other render target.
        dev.CreateSwapchainProxy();

        dev.Adapter = new AdapterInfo(
            BackendKind.Null,
            DeviceName: "Nintendo Switch (NVN2)",
            DriverInfo: "NVN — placeholder",
            VendorId: 0, DeviceId: 0,
            IsDiscrete: false);
        dev.Features = DeviceFeatures.ComputeShaders | DeviceFeatures.AnisotropicFiltering
                     | DeviceFeatures.TextureCompressionASTC | DeviceFeatures.TextureCompressionBC
                     | DeviceFeatures.MultiDrawIndirect | DeviceFeatures.IndirectFirstInstance;
        dev.Limits = new DeviceLimits(
            MaxTextureDimension2D: 16384,
            MaxTextureDimension3D: 2048,
            MaxTextureArrayLayers: 2048,
            MaxBindGroups: 4,
            MaxBindingsPerBindGroup: 31,
            MaxDynamicUniformBuffersPerPipeline: 8,
            MaxDynamicStorageBuffersPerPipeline: 4,
            MaxSampledTexturesPerShaderStage: 31,
            MaxSamplersPerShaderStage: 16,
            MaxStorageBuffersPerShaderStage: 31,
            MaxStorageTexturesPerShaderStage: 8,
            MaxUniformBuffersPerShaderStage: 14,
            MaxUniformBufferBindingSize: 65536,
            MaxStorageBufferBindingSize: 1 << 27,
            MinUniformBufferOffsetAlignment: 256,
            MinStorageBufferOffsetAlignment: 32,
            MaxVertexBuffers: 16,
            MaxVertexAttributes: 16,
            MaxVertexBufferArrayStride: 2048,
            MaxPushConstantsSize: 256,
            MaxColorAttachments: 8,
            MaxComputeWorkgroupSizeX: 1024,
            MaxComputeWorkgroupSizeY: 1024,
            MaxComputeWorkgroupSizeZ: 64,
            MaxComputeInvocationsPerWorkgroup: 1024,
            MaxSamplerAnisotropy: 16);
        return dev;
    }

    private void CreateSwapchainProxy()
    {
        var tex = new SwitchTexture
        {
            Format = _swapchainFormat,
            Width = _swapchainWidth,
            Height = _swapchainHeight,
            Depth = 1,
            MipLevels = 1,
            ArrayLayers = 1,
            SampleCount = 1,
            Usage = TextureUsage.ColorAttachment,
            Dimension = TextureDimension.Tex2D,
            IsSwapchainImage = true,
        };
        _swapchainTextureId = NewHandleId();
        _textures[_swapchainTextureId] = tex;
        var view = new SwitchTextureView
        {
            TextureId = _swapchainTextureId,
            Format = _swapchainFormat,
            Aspect = TextureAspect.All,
            BaseMip = 0, MipCount = 1,
            BaseLayer = 0, LayerCount = 1,
        };
        _swapchainViewId = NewHandleId();
        _textureViews[_swapchainViewId] = view;
        tex.DefaultViewId = _swapchainViewId;
    }

    public void ConfigureSwapchain(in SwapchainDesc desc)
        => throw NvnNotImplemented(nameof(ConfigureSwapchain),
            "nvnWindowBuilder* + nvnWindowInitialize with the new SwapchainDesc.");

    public void ResizeSwapchain(int width, int height)
        => throw NvnNotImplemented(nameof(ResizeSwapchain),
            "nvnWindowSetCrop / re-init the window when the system reports a layer-size change.");

    public void SetVSync(bool vsync)
        => throw NvnNotImplemented(nameof(SetVSync),
            "nvnWindowSetPresentInterval(NvnWindow, vsync ? 1 : 0).");

    public void WaitIdle()
        => throw NvnNotImplemented(nameof(WaitIdle),
            "nvnQueueFinish(NvnQueue) — flush all outstanding GPU work.");

    public void SetDebugLabel<T>(T handle, string label) where T : struct
        => throw NvnNotImplemented(nameof(SetDebugLabel),
            "nvnObjectSetDebugLabel via the typed object setters (one per resource kind).");

    internal ulong NewHandleId() => _nextHandleId++;

    public void Dispose()
    {
        // TODO: nvnQueueFinish, then walk every resource dictionary and call the matching
        // nvnXFinalize. Free memory pools last (textures/buffers depend on them).
    }

    internal static NotImplementedException NvnNotImplemented(string method, string nvnHint) =>
        new($"SwitchDevice.{method}: NVN2 implementation pending — {nvnHint}");
}
