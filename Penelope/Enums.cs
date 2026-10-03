namespace Penelope;

[Flags]
public enum ShaderStage
{
    None = 0,
    Vertex = 1 << 0,
    Fragment = 1 << 1,
    Compute = 1 << 2,
    All = Vertex | Fragment | Compute,
}

[Flags]
public enum BufferUsage
{
    None = 0,
    Vertex = 1 << 0,
    Index = 1 << 1,
    Uniform = 1 << 2,
    Storage = 1 << 3,
    Indirect = 1 << 4,
    CopySrc = 1 << 5,
    CopyDst = 1 << 6,
    MapRead = 1 << 7,
    MapWrite = 1 << 8,
}

public enum BufferAccess
{
    /// <summary>Content written once at creation, never updated.</summary>
    Immutable,
    /// <summary>Updated occasionally from CPU (e.g. per-frame uniforms).</summary>
    Dynamic,
    /// <summary>Updated every frame, possibly multiple times (e.g. sprite batches).</summary>
    Stream,
}

public enum IndexType
{
    UInt16,
    UInt32,
}

[Flags]
public enum TextureUsage
{
    None = 0,
    Sampled = 1 << 0,
    Storage = 1 << 1,
    ColorAttachment = 1 << 2,
    DepthStencilAttachment = 1 << 3,
    CopySrc = 1 << 4,
    CopyDst = 1 << 5,
    /// <summary>Backend may keep only on-tile memory (mobile). Invalid at pass end.</summary>
    TransientAttachment = 1 << 6,
}

public enum TextureDimension
{
    Tex2D,
    Tex2DArray,
    Tex3D,
    Cube,
    CubeArray,
}

public enum TextureFormat
{
    // 8-bit
    R8Unorm,
    R8Snorm,
    R8Uint,
    R8Sint,
    // 16-bit
    R16Float,
    R16Uint,
    R16Sint,
    Rg8Unorm,
    Rg8Snorm,
    // 32-bit
    R32Float,
    R32Uint,
    R32Sint,
    Rg16Float,
    Rg16Uint,
    Rg16Sint,
    Rgba8Unorm,
    Rgba8UnormSrgb,
    Rgba8Snorm,
    Rgba8Uint,
    Rgba8Sint,
    Bgra8Unorm,
    Bgra8UnormSrgb,
    Rgb10A2Unorm,
    Rg11B10Float,
    // 64-bit
    Rg32Float,
    Rg32Uint,
    Rg32Sint,
    Rgba16Float,
    Rgba16Uint,
    Rgba16Sint,
    // 128-bit
    Rgba32Float,
    Rgba32Uint,
    Rgba32Sint,
    // Depth / stencil
    Depth16Unorm,
    Depth24Plus,
    Depth24PlusStencil8,
    Depth32Float,
    Depth32FloatStencil8,
}

public enum TextureAspect
{
    All,
    DepthOnly,
    StencilOnly,
}

public enum FilterMode
{
    Nearest,
    Linear,
}

public enum MipmapMode
{
    Nearest,
    Linear,
}

public enum AddressMode
{
    ClampToEdge,
    Repeat,
    MirrorRepeat,
    ClampToBorder,
}

public enum BorderColor
{
    TransparentBlack,
    OpaqueBlack,
    OpaqueWhite,
}

public enum CompareFunc
{
    Never,
    Less,
    Equal,
    LessEqual,
    Greater,
    NotEqual,
    GreaterEqual,
    Always,
}

public enum PrimitiveTopology
{
    PointList,
    LineList,
    LineStrip,
    TriangleList,
    TriangleStrip,
    TriangleFan,
}

public enum CullMode
{
    None,
    Front,
    Back,
}

public enum FrontFace
{
    Ccw,
    Cw,
}

public enum PolygonMode
{
    Fill,
    Line,
    Point,
}

public enum BlendFactor
{
    Zero,
    One,
    SrcColor,
    OneMinusSrcColor,
    SrcAlpha,
    OneMinusSrcAlpha,
    DstColor,
    OneMinusDstColor,
    DstAlpha,
    OneMinusDstAlpha,
    ConstantColor,
    OneMinusConstantColor,
    ConstantAlpha,
    OneMinusConstantAlpha,
    SrcAlphaSaturated,
    Src1Color,
    OneMinusSrc1Color,
    Src1Alpha,
    OneMinusSrc1Alpha,
}

public enum BlendOp
{
    Add,
    Subtract,
    ReverseSubtract,
    Min,
    Max,
}

[Flags]
public enum ColorWriteMask
{
    None = 0,
    R = 1 << 0,
    G = 1 << 1,
    B = 1 << 2,
    A = 1 << 3,
    All = R | G | B | A,
}

public enum StencilOp
{
    Keep,
    Zero,
    Replace,
    IncrementClamp,
    DecrementClamp,
    Invert,
    IncrementWrap,
    DecrementWrap,
}

public enum VertexFormat
{
    Float1,
    Float2,
    Float3,
    Float4,
    UByte2,
    UByte4,
    UByte2Norm,
    UByte4Norm,
    Byte2Norm,
    Byte4Norm,
    UShort2,
    UShort4,
    UShort2Norm,
    UShort4Norm,
    Short2Norm,
    Short4Norm,
    Half2,
    Half4,
    UInt1,
    UInt2,
    UInt3,
    UInt4,
    Int1,
    Int2,
    Int3,
    Int4,
}

public enum VertexStepMode
{
    PerVertex,
    PerInstance,
}

public enum BindingType
{
    UniformBuffer,
    StorageBuffer,
    ReadOnlyStorageBuffer,
    Sampler,
    ComparisonSampler,
    SampledTexture,
    StorageTexture,
    ReadOnlyStorageTexture,
    /// <summary>
    ///     Combined image+sampler (Vulkan VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER). GLSL
    ///     <c>uniform sampler2D</c> compiles to this. Supply both a texture view AND a sampler
    ///     on the <see cref="BindGroupEntry"/>.
    /// </summary>
    CombinedImageSampler,
}

public enum LoadOp
{
    /// <summary>Preserve existing contents.</summary>
    Load,
    /// <summary>Fill with the clear value.</summary>
    Clear,
    /// <summary>Contents undefined at pass begin; lets tiled GPUs skip the initial load.</summary>
    DontCare,
}

public enum StoreOp
{
    /// <summary>Write results back to memory.</summary>
    Store,
    /// <summary>Results may be discarded; lets tiled GPUs skip the final store.</summary>
    DontCare,
}

public enum PresentMode
{
    /// <summary>VSync on. Always waits for vblank. No tearing, may add input latency.</summary>
    Fifo,
    /// <summary>VSync on, but late frames present immediately (may tear on late frame).</summary>
    FifoRelaxed,
    /// <summary>Triple-buffered; newest finished frame wins. No tearing, low latency.</summary>
    Mailbox,
    /// <summary>No sync. Lowest latency, may tear.</summary>
    Immediate,
}

public enum QueryType
{
    Timestamp,
    Occlusion,
    PipelineStatistics,
}
