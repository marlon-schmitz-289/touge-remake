namespace Penelope;

/// <summary>
///     Opaque GPU resource handle. Backends map the <see cref="Id"/> to their own object store.
///     Always pass handles by value; copying is cheap and safe. A zero-initialized handle is the
///     "null" handle (see <c>Null</c> on each type).
/// </summary>
public readonly record struct BufferHandle(ulong Id)
{
    public static BufferHandle Null => default;
    public bool IsNull => Id == 0;
}

public readonly record struct TextureHandle(ulong Id)
{
    public static TextureHandle Null => default;
    public bool IsNull => Id == 0;
}

/// <summary>View into a <see cref="TextureHandle"/> — selects mip range, array slice range, or aspect.</summary>
public readonly record struct TextureViewHandle(ulong Id)
{
    public static TextureViewHandle Null => default;
    public bool IsNull => Id == 0;
}

public readonly record struct SamplerHandle(ulong Id)
{
    public static SamplerHandle Null => default;
    public bool IsNull => Id == 0;
}

public readonly record struct ShaderHandle(ulong Id)
{
    public static ShaderHandle Null => default;
    public bool IsNull => Id == 0;
}

public readonly record struct RenderPipelineHandle(ulong Id)
{
    public static RenderPipelineHandle Null => default;
    public bool IsNull => Id == 0;
}

public readonly record struct ComputePipelineHandle(ulong Id)
{
    public static ComputePipelineHandle Null => default;
    public bool IsNull => Id == 0;
}

/// <summary>Describes the shape of a bind group — which bindings exist and their types.</summary>
public readonly record struct BindGroupLayoutHandle(ulong Id)
{
    public static BindGroupLayoutHandle Null => default;
    public bool IsNull => Id == 0;
}

/// <summary>A concrete set of resources (buffers, textures, samplers) conforming to a layout.</summary>
public readonly record struct BindGroupHandle(ulong Id)
{
    public static BindGroupHandle Null => default;
    public bool IsNull => Id == 0;
}

public readonly record struct RenderTargetHandle(ulong Id)
{
    public static RenderTargetHandle Null => default;
    public bool IsNull => Id == 0;
}

/// <summary>Pool of GPU timestamp / occlusion queries.</summary>
public readonly record struct QueryPoolHandle(ulong Id)
{
    public static QueryPoolHandle Null => default;
    public bool IsNull => Id == 0;
}

/// <summary>CPU-side fence signaled when submitted GPU work completes.</summary>
public readonly record struct FenceHandle(ulong Id)
{
    public static FenceHandle Null => default;
    public bool IsNull => Id == 0;
}
