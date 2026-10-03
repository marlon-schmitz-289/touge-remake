using System.Runtime.Versioning;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using SharpMetal.QuartzCore;

namespace Penelope.Backends.Metal;

/// <summary>
///     Metal backend for Penelope. Apple-only — runs on macOS (ships with Metal) and iOS
///     (the same SharpMetal API). Linux/Windows can compile this assembly (the managed code
///     loads fine), but <see cref="Create"/> will fail because Metal.framework isn't present
///     on those platforms.
///
///     Construction needs a host-supplied <see cref="MetalContextProvider"/> that wraps the
///     CAMetalLayer the windowing library has set up (SDL_Metal_GetLayer in SDL2). Penelope
///     never creates the layer itself — the host knows the NSView / NSWindow / iOS UIView.
///
///     Coordinate / sampling conventions match Vulkan (top-left clip origin, depth in [0,1],
///     UV (0,0) = top of texture). The same shaders used by the Vulkan and OpenGL backends
///     are translated to MSL by SPIRV-Cross at <see cref="CreateShader"/> time.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed unsafe partial class MetalDevice : IPenelopeDevice
{
    /// <summary>
    ///     Host bridge that gives Penelope the CAMetalLayer + its drawable size. The layer's
    ///     <c>device</c> property must already be set to the same MTLDevice we use here, or
    ///     <c>NextDrawable</c> will return null. <see cref="GetDrawableSize"/> is queried
    ///     every frame in case the host resizes the layer.
    /// </summary>
    public sealed class MetalContextProvider
    {
        public required CAMetalLayer Layer { get; init; }
        public required Func<(int width, int height)> GetDrawableSize { get; init; }
    }

    internal MTLDevice Device;
    internal MTLCommandQueue Queue;
    internal CAMetalLayer Layer;
    private readonly MetalContextProvider _context;

    private readonly HandleTable<MetalBuffer> _buffers = new("Buffer");
    private readonly HandleTable<MetalTexture> _textures = new("Texture");
    private readonly HandleTable<MetalTextureView> _textureViews = new("TextureView");
    private readonly HandleTable<MetalSampler> _samplers = new("Sampler");
    private readonly Dictionary<SamplerDesc, SamplerHandle> _samplerCache = new();
    private readonly HandleTable<MetalShader> _shaders = new("Shader");
    private readonly HandleTable<MetalRenderPipeline> _renderPipelines = new("RenderPipeline");
    private readonly HandleTable<MetalComputePipeline> _computePipelines = new("ComputePipeline");
    private readonly HandleTable<MetalBindGroupLayout> _bindGroupLayouts = new("BindGroupLayout");
    private readonly Dictionary<BindGroupLayoutKey, BindGroupLayoutHandle> _bgLayoutCache = new();
    private readonly HandleTable<MetalBindGroup> _bindGroups = new("BindGroup");
    private readonly HandleTable<MetalRenderTarget> _renderTargets = new("RenderTarget");
    private readonly HandleTable<MetalQueryPool> _queryPools = new("QueryPool");
    private readonly HandleTable<MetalFence> _fences = new("Fence");

    private ulong _nextHandleId = 1;

    private TextureFormat _swapchainFormat;
    private PresentMode _presentMode;
    private int _swapchainWidth;
    private int _swapchainHeight;
    private ulong _swapchainTextureId;
    private ulong _swapchainViewId;

    private bool _frameActive;
    /// <summary>Drawable for the current frame, between BeginFrame and EndFrame. Released on present.</summary>
    private CAMetalDrawable _currentDrawable;
    /// <summary>Command buffer recording for the current frame (one per frame is the simple model).</summary>
    private MTLCommandBuffer _currentCommandBuffer;

    // Per-frame slot tracking. SharpMetal doesn't surface AddCompletedHandler, so we keep the
    // last-submitted command buffer per slot and WaitUntilCompleted on it at BeginFrame.
    // Functionally equivalent to a dispatch_semaphore — the slot's per-frame buffers (e.g.
    // TransientBufferRing slabs) are GPU-free by the time BeginFrame returns.
    private const int FramesInFlightCount = 2;
    private readonly MTLCommandBuffer[] _slotBuffers = new MTLCommandBuffer[FramesInFlightCount];
    private int _frameSlot;

    public BackendKind Backend => BackendKind.Metal;
    public int FramesInFlight => FramesInFlightCount;
    public int CurrentFrameSlot => _frameSlot;
    public AdapterInfo Adapter { get; private set; }
    public DeviceFeatures Features { get; private set; }
    public DeviceLimits Limits { get; private set; }

    public int SwapchainWidth => _swapchainWidth;
    public int SwapchainHeight => _swapchainHeight;
    public TextureFormat SwapchainFormat => _swapchainFormat;
    public PresentMode PresentMode => _presentMode;

    public TextureViewHandle CurrentSwapchainView => new(_swapchainViewId);

    /// <summary>Metal exposes timestamps in nanoseconds via MTLCounterSampleBuffer; we report 1.0 by convention.</summary>
    public double TimestampPeriodNs => 1.0;

    private MetalDevice(MetalContextProvider context)
    {
        _context = context;
        Device = MTLDevice.CreateSystemDefaultDevice();
        if (Device.NativePtr == 0)
            throw new InvalidOperationException("MTLCreateSystemDefaultDevice returned null — no Metal-capable GPU?");
        Queue = Device.NewCommandQueue();
        Layer = context.Layer;
    }

    /// <summary>
    ///     Create a Metal device. The caller is responsible for setting up the CAMetalLayer
    ///     on its NSView/UIView before calling this; we read the device off the layer if it's
    ///     already set, otherwise we install ours.
    /// </summary>
    public static MetalDevice Create(
        MetalContextProvider context,
        in DeviceDesc deviceDesc,
        in SwapchainDesc swapchainDesc)
    {
        var dev = new MetalDevice(context);
        // Pin the layer to our device so NextDrawable returns textures from the same device.
        dev.Layer.Device = dev.Device;
        dev.Layer.PixelFormat = MetalConvert.ToMetal(swapchainDesc.Format);
        dev.Layer.FramebufferOnly = true;
        dev.Layer.DisplaySyncEnabled = swapchainDesc.PresentMode != Penelope.PresentMode.Immediate;

        dev._swapchainFormat = swapchainDesc.Format;
        dev._presentMode = swapchainDesc.PresentMode;
        var (sw, sh) = context.GetDrawableSize();
        dev._swapchainWidth = sw > 0 ? sw : swapchainDesc.Width;
        dev._swapchainHeight = sh > 0 ? sh : swapchainDesc.Height;
        dev.CreateSwapchainProxy();

        dev.Adapter = new AdapterInfo(
            BackendKind.Metal,
            DeviceName: dev.Device.Name.ToString() ?? "Metal Device",
            DriverInfo: dev.Device.HasUnifiedMemory ? "unified-memory" : "discrete",
            VendorId: 0,
            DeviceId: 0,
            IsDiscrete: !dev.Device.IsLowPower);
        dev.Features = dev.BuildFeatures();
        dev.Limits = dev.BuildLimits();
        return dev;
    }

    private void CreateSwapchainProxy()
    {
        // The swapchain "texture" is a placeholder — the real MTLTexture comes from
        // CAMetalDrawable.Texture once per frame in BeginFrame and is patched into this slot.
        var tex = new MetalTexture
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
            IsSwapchainProxy = true,
        };
        _swapchainTextureId = NewHandleId();
        _textures[_swapchainTextureId] = tex;

        var view = new MetalTextureView
        {
            TextureId = _swapchainTextureId,
            Format = _swapchainFormat,
            Aspect = TextureAspect.All,
            BaseMip = 0,
            MipCount = 1,
            BaseLayer = 0,
            LayerCount = 1,
        };
        _swapchainViewId = NewHandleId();
        _textureViews[_swapchainViewId] = view;
        tex.DefaultViewId = _swapchainViewId;
    }

    private DeviceFeatures BuildFeatures()
    {
        // Conservative baseline that's true on any Apple Silicon / Intel Metal-capable GPU.
        return DeviceFeatures.ComputeShaders | DeviceFeatures.AnisotropicFiltering
             | DeviceFeatures.IndirectFirstInstance | DeviceFeatures.MultiDrawIndirect
             | DeviceFeatures.TextureCompressionASTC | DeviceFeatures.TextureCompressionBC
             | DeviceFeatures.DepthBiasClamp;
    }

    private DeviceLimits BuildLimits()
    {
        // Limits roughly matching Apple GPU family 4+ (Apple Silicon / A11+). The exact values
        // could be queried per-device via supportsFamily/maxTextureWidth2DPerArray etc., but
        // for the common cases these are accurate enough for engine sanity-checking.
        return new DeviceLimits(
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
            MaxUniformBuffersPerShaderStage: 31,
            MaxUniformBufferBindingSize: (long)Device.MaxBufferLength,
            MaxStorageBufferBindingSize: (long)Device.MaxBufferLength,
            MinUniformBufferOffsetAlignment: 256,
            MinStorageBufferOffsetAlignment: 256,
            MaxVertexBuffers: 31,
            MaxVertexAttributes: 31,
            MaxVertexBufferArrayStride: 2048,
            MaxPushConstantsSize: 4096,
            MaxColorAttachments: 8,
            MaxComputeWorkgroupSizeX: (int)Device.MaxThreadsPerThreadgroup.width,
            MaxComputeWorkgroupSizeY: (int)Device.MaxThreadsPerThreadgroup.height,
            MaxComputeWorkgroupSizeZ: (int)Device.MaxThreadsPerThreadgroup.depth,
            MaxComputeInvocationsPerWorkgroup: (int)(Device.MaxThreadsPerThreadgroup.width * Device.MaxThreadsPerThreadgroup.height),
            MaxSamplerAnisotropy: 16);
    }

    public void ConfigureSwapchain(in SwapchainDesc desc)
    {
        _swapchainFormat = desc.Format;
        _presentMode = desc.PresentMode;
        Layer.PixelFormat = MetalConvert.ToMetal(desc.Format);
        Layer.DisplaySyncEnabled = desc.PresentMode != Penelope.PresentMode.Immediate;
        ResizeSwapchain(desc.Width, desc.Height);
    }

    public void ResizeSwapchain(int width, int height)
    {
        var (dw, dh) = _context.GetDrawableSize();
        if (dw > 0 && dh > 0) { width = dw; height = dh; }
        _swapchainWidth = width;
        _swapchainHeight = height;
        if (_textures.TryGetValue(_swapchainTextureId, out var tex))
        {
            tex.Width = width;
            tex.Height = height;
        }
    }

    public void SetVSync(bool vsync)
    {
        _presentMode = vsync ? Penelope.PresentMode.Fifo : Penelope.PresentMode.Immediate;
        Layer.DisplaySyncEnabled = vsync;
    }

    internal ulong NewHandleId() => _nextHandleId++;

    public void WaitIdle()
    {
        // Equivalent to vkDeviceWaitIdle: submit an empty command buffer and block on completion.
        var cb = Queue.CommandBuffer();
        cb.Commit();
        cb.WaitUntilCompleted();
    }

    public void SetDebugLabel<T>(T handle, string label) where T : struct
    {
        // MTLBuffer/MTLTexture/etc. have a Label setter — wiring is mechanical, omitted here.
    }

    public void Dispose()
    {
        WaitIdle();
        foreach (var b in _buffers.Values) b.Buffer.Dispose();
        foreach (var t in _textures.Values) if (!t.IsSwapchainProxy) t.Texture.Dispose();
        foreach (var v in _textureViews.Values) if (v.HasViewTexture) v.ViewTexture.Dispose();
        foreach (var s in _samplers.Values) s.Sampler.Dispose();
        foreach (var sh in _shaders.Values)
        {
            if (sh.HasVertex) { sh.VertexFunction.Dispose(); sh.VertexLibrary.Dispose(); }
            if (sh.HasFragment) { sh.FragmentFunction.Dispose(); sh.FragmentLibrary.Dispose(); }
            if (sh.HasCompute) { sh.ComputeFunction.Dispose(); sh.ComputeLibrary.Dispose(); }
        }
        foreach (var p in _renderPipelines.Values)
        {
            p.Pipeline.Dispose();
            if (p.HasDepthStencilState) p.DepthStencilState.Dispose();
        }
        foreach (var p in _computePipelines.Values) p.Pipeline.Dispose();
        foreach (var q in _queryPools.Values) q.Backing.Dispose();
        foreach (var f in _fences.Values) f.Event.Dispose();
        Queue.Dispose();
        Device.Dispose();
    }

    internal MetalBuffer GetBuffer(BufferHandle h) => _buffers[h.Id];
    internal MetalTexture GetTexture(ulong id) => _textures[id];
    internal MetalTextureView GetTextureView(TextureViewHandle h) => _textureViews[h.Id];
    internal MetalSampler GetSampler(SamplerHandle h) => _samplers[h.Id];
    internal MetalBindGroup GetBindGroup(BindGroupHandle h) => _bindGroups[h.Id];
    internal MetalBindGroupLayout GetBindGroupLayout(ulong id) => _bindGroupLayouts[id];
    internal MetalRenderPipeline GetRenderPipeline(RenderPipelineHandle h) => _renderPipelines[h.Id];
    internal MetalComputePipeline GetComputePipeline(ComputePipelineHandle h) => _computePipelines[h.Id];
    internal MetalQueryPool GetQueryPool(QueryPoolHandle h) => _queryPools[h.Id];

    /// <summary>Resolve the actual MTLTexture behind a Penelope view (with view-texture fallback).</summary>
    internal MTLTexture GetViewTexture(MetalTextureView view)
    {
        if (view.HasViewTexture) return view.ViewTexture;
        return _textures[view.TextureId].Texture;
    }
}
