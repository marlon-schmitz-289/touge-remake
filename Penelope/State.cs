namespace Penelope;

/// <summary>
///     Per-attachment color blend configuration. <see cref="Enabled"/> = false short-circuits the
///     whole block to opaque writes.
/// </summary>
public readonly record struct BlendState(
    bool Enabled,
    BlendFactor SrcColor,
    BlendFactor DstColor,
    BlendOp ColorOp,
    BlendFactor SrcAlpha,
    BlendFactor DstAlpha,
    BlendOp AlphaOp,
    ColorWriteMask WriteMask)
{
    public static BlendState Opaque => new(
        false,
        BlendFactor.One, BlendFactor.Zero, BlendOp.Add,
        BlendFactor.One, BlendFactor.Zero, BlendOp.Add,
        ColorWriteMask.All);

    public static BlendState AlphaBlend => new(
        true,
        BlendFactor.SrcAlpha, BlendFactor.OneMinusSrcAlpha, BlendOp.Add,
        BlendFactor.One, BlendFactor.OneMinusSrcAlpha, BlendOp.Add,
        ColorWriteMask.All);

    public static BlendState Premultiplied => new(
        true,
        BlendFactor.One, BlendFactor.OneMinusSrcAlpha, BlendOp.Add,
        BlendFactor.One, BlendFactor.OneMinusSrcAlpha, BlendOp.Add,
        ColorWriteMask.All);

    public static BlendState Additive => new(
        true,
        BlendFactor.SrcAlpha, BlendFactor.One, BlendOp.Add,
        BlendFactor.One, BlendFactor.One, BlendOp.Add,
        ColorWriteMask.All);
}

public readonly record struct StencilFaceState(
    CompareFunc Compare,
    StencilOp FailOp,
    StencilOp DepthFailOp,
    StencilOp PassOp)
{
    public static StencilFaceState Default => new(CompareFunc.Always, StencilOp.Keep, StencilOp.Keep, StencilOp.Keep);
}

public readonly record struct DepthStencilState(
    bool DepthTestEnabled,
    bool DepthWriteEnabled,
    CompareFunc DepthCompare,
    bool StencilEnabled,
    byte StencilReadMask,
    byte StencilWriteMask,
    StencilFaceState StencilFront,
    StencilFaceState StencilBack,
    float DepthBiasConstant,
    float DepthBiasSlope,
    float DepthBiasClamp)
{
    public static DepthStencilState Disabled => new(
        false, false, CompareFunc.Always,
        false, 0xFF, 0xFF, StencilFaceState.Default, StencilFaceState.Default,
        0f, 0f, 0f);

    public static DepthStencilState DepthLessWrite => new(
        true, true, CompareFunc.Less,
        false, 0xFF, 0xFF, StencilFaceState.Default, StencilFaceState.Default,
        0f, 0f, 0f);

    public static DepthStencilState DepthLessReadOnly => new(
        true, false, CompareFunc.Less,
        false, 0xFF, 0xFF, StencilFaceState.Default, StencilFaceState.Default,
        0f, 0f, 0f);
}

public readonly record struct RasterizerState(
    CullMode Cull,
    FrontFace FrontFace,
    PolygonMode Polygon,
    bool DepthClampEnabled,
    bool ScissorEnabled,
    float LineWidth)
{
    public static RasterizerState Default => new(
        CullMode.None, FrontFace.Ccw, PolygonMode.Fill,
        false, true, 1f);

    public static RasterizerState BackfaceCull => Default with { Cull = CullMode.Back };
}

public readonly record struct MultisampleState(
    int SampleCount,
    uint SampleMask,
    bool AlphaToCoverageEnabled)
{
    public static MultisampleState Disabled => new(1, 0xFFFFFFFFu, false);
}

/// <summary>Per-attribute description for a single vertex buffer slot.</summary>
public readonly record struct VertexAttribute(
    int ShaderLocation,
    VertexFormat Format,
    int OffsetBytes);

/// <summary>
///     One vertex buffer input slot. <see cref="StepMode"/> distinguishes per-vertex vs per-instance
///     data. The pipeline binds <c>BufferSlot</c> from the encoder's currently-bound vertex buffers.
/// </summary>
public readonly record struct VertexBufferLayout(
    int BufferSlot,
    int StrideBytes,
    VertexStepMode StepMode,
    VertexAttribute[] Attributes);

/// <summary>Collected vertex input layout — one or more buffer slots.</summary>
public sealed class VertexLayout
{
    public VertexLayout(params VertexBufferLayout[] buffers)
    {
        Buffers = buffers;
    }

    public VertexBufferLayout[] Buffers { get; }

    /// <summary>Convenience: single-buffer per-vertex layout.</summary>
    public static VertexLayout Interleaved(int strideBytes, params VertexAttribute[] attributes)
    {
        return new VertexLayout(new VertexBufferLayout(0, strideBytes, VertexStepMode.PerVertex, attributes));
    }
}

/// <summary>Per-color-attachment pipeline target state.</summary>
public readonly record struct ColorTargetState(
    TextureFormat Format,
    BlendState Blend);
