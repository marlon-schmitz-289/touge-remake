namespace Penelope;

public enum BackendKind
{
    OpenGL,
    Vulkan,
    Metal,
    D3D12,
    WebGpu,
    Null,
}

public enum AdapterPreference
{
    /// <summary>Integrated GPU / power savings.</summary>
    LowPower,
    /// <summary>Discrete GPU / best performance.</summary>
    HighPerformance,
    /// <summary>CPU reference driver (software). For testing.</summary>
    Software,
}

public readonly record struct AdapterInfo(
    BackendKind Backend,
    string DeviceName,
    string DriverInfo,
    uint VendorId,
    uint DeviceId,
    bool IsDiscrete);

[Flags]
public enum DeviceFeatures : ulong
{
    None = 0,
    DepthClamping = 1 << 0,
    TextureCompressionBC = 1 << 1,
    TextureCompressionETC2 = 1 << 2,
    TextureCompressionASTC = 1 << 3,
    TimestampQuery = 1 << 4,
    PipelineStatisticsQuery = 1 << 5,
    ShaderFloat64 = 1 << 6,
    ShaderInt64 = 1 << 7,
    ShaderInt16 = 1 << 8,
    AnisotropicFiltering = 1 << 9,
    IndirectFirstInstance = 1 << 10,
    MultiDrawIndirect = 1 << 11,
    DepthBiasClamp = 1 << 12,
    DualSourceBlending = 1 << 13,
    ComputeShaders = 1 << 14,
    GeometryShaders = 1 << 15,
    TessellationShaders = 1 << 16,
    BindlessResources = 1 << 17,
}

public readonly record struct DeviceLimits(
    int MaxTextureDimension2D,
    int MaxTextureDimension3D,
    int MaxTextureArrayLayers,
    int MaxBindGroups,
    int MaxBindingsPerBindGroup,
    int MaxDynamicUniformBuffersPerPipeline,
    int MaxDynamicStorageBuffersPerPipeline,
    int MaxSampledTexturesPerShaderStage,
    int MaxSamplersPerShaderStage,
    int MaxStorageBuffersPerShaderStage,
    int MaxStorageTexturesPerShaderStage,
    int MaxUniformBuffersPerShaderStage,
    long MaxUniformBufferBindingSize,
    long MaxStorageBufferBindingSize,
    int MinUniformBufferOffsetAlignment,
    int MinStorageBufferOffsetAlignment,
    int MaxVertexBuffers,
    int MaxVertexAttributes,
    int MaxVertexBufferArrayStride,
    int MaxPushConstantsSize,
    int MaxColorAttachments,
    int MaxComputeWorkgroupSizeX,
    int MaxComputeWorkgroupSizeY,
    int MaxComputeWorkgroupSizeZ,
    int MaxComputeInvocationsPerWorkgroup,
    float MaxSamplerAnisotropy);

public readonly record struct DeviceDesc(
    AdapterPreference Preference = AdapterPreference.HighPerformance,
    DeviceFeatures RequiredFeatures = DeviceFeatures.None,
    bool EnableValidation = false,
    bool EnableDebugMarkers = false,
    string? DebugName = null);

public readonly record struct SwapchainDesc(
    int Width,
    int Height,
    TextureFormat Format,
    PresentMode PresentMode,
    // Triple-buffer by default. With FIFO (VSync on) a double-buffered swapchain drops to half
    // refresh the moment a single frame runs long — the classic VSync "lag"/stutter. A third
    // image gives the GPU a spare to work on so a late frame doesn't immediately halve the rate.
    // The Vulkan backend clamps this into [caps.MinImageCount, caps.MaxImageCount].
    int MinImageCount = 3);
