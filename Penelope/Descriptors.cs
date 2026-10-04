namespace Penelope;

public readonly record struct BufferDesc(
    int SizeBytes,
    BufferUsage Usage,
    BufferAccess Access = BufferAccess.Dynamic,
    string? DebugName = null);

public readonly record struct TextureDesc(
    int Width,
    int Height,
    TextureFormat Format,
    TextureUsage Usage,
    TextureDimension Dimension = TextureDimension.Tex2D,
    int Depth = 1,
    int MipLevels = 1,
    int ArrayLayers = 1,
    int SampleCount = 1,
    string? DebugName = null)
{
    public static TextureDesc Sampled2D(int w, int h, TextureFormat fmt, int mips = 1, string? name = null) =>
        new(w, h, fmt, TextureUsage.Sampled | TextureUsage.CopyDst, MipLevels: mips, DebugName: name);

    public static TextureDesc ColorAttachment(int w, int h, TextureFormat fmt, string? name = null) =>
        new(w, h, fmt, TextureUsage.ColorAttachment | TextureUsage.Sampled, DebugName: name);

    public static TextureDesc DepthAttachment(int w, int h, TextureFormat fmt, string? name = null) =>
        new(w, h, fmt, TextureUsage.DepthStencilAttachment, DebugName: name);
}

public readonly record struct TextureViewDesc(
    TextureFormat? Format,
    TextureDimension? Dimension,
    TextureAspect Aspect,
    int BaseMipLevel,
    int MipLevelCount,
    int BaseArrayLayer,
    int ArrayLayerCount,
    string? DebugName = null)
{
    public static TextureViewDesc Default => new(
        null, null, TextureAspect.All, 0, 0, 0, 0, null);
}

public readonly record struct SamplerDesc(
    FilterMode MinFilter,
    FilterMode MagFilter,
    MipmapMode Mipmap,
    AddressMode AddressU,
    AddressMode AddressV,
    AddressMode AddressW,
    float LodMinClamp,
    float LodMaxClamp,
    float MaxAnisotropy,
    CompareFunc? Compare,
    BorderColor Border,
    string? DebugName = null)
{
    public static SamplerDesc Nearest => new(
        FilterMode.Nearest, FilterMode.Nearest, MipmapMode.Nearest,
        AddressMode.ClampToEdge, AddressMode.ClampToEdge, AddressMode.ClampToEdge,
        0f, 0f, 1f, null, BorderColor.TransparentBlack, null);

    public static SamplerDesc Linear => new(
        FilterMode.Linear, FilterMode.Linear, MipmapMode.Linear,
        AddressMode.ClampToEdge, AddressMode.ClampToEdge, AddressMode.ClampToEdge,
        0f, 1024f, 1f, null, BorderColor.TransparentBlack, null);

    public static SamplerDesc LinearWrap => Linear with
    {
        AddressU = AddressMode.Repeat,
        AddressV = AddressMode.Repeat,
        AddressW = AddressMode.Repeat,
    };
}

/// <summary>One entry in a bind group layout — slot index + resource type + stage visibility.</summary>
public readonly record struct BindGroupLayoutEntry(
    int Binding,
    BindingType Type,
    ShaderStage Visibility,
    /// <summary>For UniformBuffer/StorageBuffer: minimum size. 0 = no minimum.</summary>
    int MinBufferSize = 0,
    /// <summary>Dynamic offset allowed on bind (uniform/storage buffers only).</summary>
    bool HasDynamicOffset = false);

public readonly record struct BindGroupLayoutDesc(
    BindGroupLayoutEntry[] Entries,
    string? DebugName = null);

/// <summary>One concrete binding in a BindGroup — which resource fills a layout slot.</summary>
public readonly record struct BindGroupEntry(
    int Binding,
    /// <summary>Only one of Buffer / TextureView / Sampler is valid per entry; the others are Null.</summary>
    BufferHandle Buffer = default,
    int BufferOffset = 0,
    int BufferSize = 0,
    TextureViewHandle TextureView = default,
    SamplerHandle Sampler = default)
{
    public static BindGroupEntry UniformBuffer(int binding, BufferHandle buffer, int offset = 0, int size = 0) =>
        new(binding, buffer, offset, size);

    public static BindGroupEntry Texture(int binding, TextureViewHandle view) =>
        new(binding, TextureView: view);

    public static BindGroupEntry SamplerBinding(int binding, SamplerHandle sampler) =>
        new(binding, Sampler: sampler);

    /// <summary>Combined image+sampler — matches GLSL <c>uniform sampler2D</c> bindings.</summary>
    public static BindGroupEntry CombinedImageSampler(int binding, TextureViewHandle view, SamplerHandle sampler) =>
        new(binding, TextureView: view, Sampler: sampler);
}

public readonly record struct BindGroupDesc(
    BindGroupLayoutHandle Layout,
    BindGroupEntry[] Entries,
    string? DebugName = null);

/// <summary>
///     Pipeline layout = ordered list of bind group layouts + push-constant ranges. Pipelines
///     with compatible layouts can share bound bind groups without rebinding.
/// </summary>
public readonly record struct PushConstantRange(
    ShaderStage Visibility,
    int OffsetBytes,
    int SizeBytes);

public readonly record struct RenderPipelineDesc(
    ShaderHandle Shader,
    VertexLayout VertexLayout,
    PrimitiveTopology Topology,
    RasterizerState Rasterizer,
    DepthStencilState DepthStencil,
    MultisampleState Multisample,
    ColorTargetState[] ColorTargets,
    TextureFormat? DepthStencilFormat,
    BindGroupLayoutHandle[] BindGroupLayouts,
    PushConstantRange[] PushConstants,
    string? DebugName = null);

public readonly record struct ComputePipelineDesc(
    ShaderHandle Shader,
    BindGroupLayoutHandle[] BindGroupLayouts,
    PushConstantRange[] PushConstants,
    string? DebugName = null);

public readonly record struct RenderTargetDesc(
    int Width,
    int Height,
    TextureFormat ColorFormat,
    TextureFormat? DepthStencilFormat = null,
    int SampleCount = 1,
    FilterMode ColorSampleFilter = FilterMode.Linear,
    string? DebugName = null);

/// <summary>Clear color value for a color attachment. Float in [0,1] for UNORM, raw otherwise.</summary>
public readonly record struct ClearColor(float R, float G, float B, float A)
{
    public static ClearColor Black => new(0f, 0f, 0f, 1f);
    public static ClearColor Transparent => new(0f, 0f, 0f, 0f);
}

public readonly record struct ColorAttachment(
    TextureViewHandle View,
    LoadOp Load,
    StoreOp Store,
    ClearColor ClearValue,
    /// <summary>MSAA resolve target. Null if <see cref="View"/> is single-sample.</summary>
    TextureViewHandle ResolveTarget = default);

public readonly record struct DepthStencilAttachment(
    TextureViewHandle View,
    LoadOp DepthLoad,
    StoreOp DepthStore,
    float DepthClear,
    LoadOp StencilLoad,
    StoreOp StencilStore,
    byte StencilClear,
    bool DepthReadOnly,
    bool StencilReadOnly,
    /// <summary>MSAA depth resolve target (sample 0), single-sample. Metal and Vulkan (SAMPLE_ZERO); OpenGL ignores it.</summary>
    TextureViewHandle ResolveTarget = default);

public readonly record struct RenderPassDesc(
    ColorAttachment[] ColorAttachments,
    DepthStencilAttachment? DepthStencilAttachment = null,
    QueryPoolHandle OcclusionQuerySet = default,
    string? DebugName = null);

public readonly record struct QueryPoolDesc(
    QueryType Type,
    int Count,
    string? DebugName = null);
