using Silk.NET.OpenGL;
using GLBlendOp = Silk.NET.OpenGL.BlendEquationModeEXT;
using GLStencilOp = Silk.NET.OpenGL.StencilOp;
using GLPolygonMode = Silk.NET.OpenGL.PolygonMode;
using GLPrimitiveType = Silk.NET.OpenGL.PrimitiveType;

namespace Penelope.Backends.OpenGL;

/// <summary>
///     Penelope ↔ OpenGL enum/format conversions. Mirrors VulkanConvert. Sizing/byte counts are
///     authoritative — the OpenGL backend uses these when uploading textures and computing
///     vertex strides.
/// </summary>
internal static class OpenGLConvert
{
    /// <summary>(internalFormat, format, type, bytesPerPixel) — used by glTexImage2D / glTexSubImage2D.</summary>
    public static (InternalFormat Internal, PixelFormat Format, PixelType Type, int Bpp) ToGl(TextureFormat fmt) => fmt switch
    {
        TextureFormat.R8Unorm => (InternalFormat.R8, PixelFormat.Red, PixelType.UnsignedByte, 1),
        TextureFormat.R8Snorm => (InternalFormat.R8SNorm, PixelFormat.Red, PixelType.Byte, 1),
        TextureFormat.R8Uint => (InternalFormat.R8ui, PixelFormat.RedInteger, PixelType.UnsignedByte, 1),
        TextureFormat.R8Sint => (InternalFormat.R8i, PixelFormat.RedInteger, PixelType.Byte, 1),
        TextureFormat.R16Float => (InternalFormat.R16f, PixelFormat.Red, PixelType.HalfFloat, 2),
        TextureFormat.R16Uint => (InternalFormat.R16ui, PixelFormat.RedInteger, PixelType.UnsignedShort, 2),
        TextureFormat.R16Sint => (InternalFormat.R16i, PixelFormat.RedInteger, PixelType.Short, 2),
        TextureFormat.Rg8Unorm => (InternalFormat.RG8, PixelFormat.RG, PixelType.UnsignedByte, 2),
        TextureFormat.Rg8Snorm => (InternalFormat.RG8SNorm, PixelFormat.RG, PixelType.Byte, 2),
        TextureFormat.R32Float => (InternalFormat.R32f, PixelFormat.Red, PixelType.Float, 4),
        TextureFormat.R32Uint => (InternalFormat.R32ui, PixelFormat.RedInteger, PixelType.UnsignedInt, 4),
        TextureFormat.R32Sint => (InternalFormat.R32i, PixelFormat.RedInteger, PixelType.Int, 4),
        TextureFormat.Rg16Float => (InternalFormat.RG16f, PixelFormat.RG, PixelType.HalfFloat, 4),
        TextureFormat.Rg16Uint => (InternalFormat.RG16ui, PixelFormat.RGInteger, PixelType.UnsignedShort, 4),
        TextureFormat.Rg16Sint => (InternalFormat.RG16i, PixelFormat.RGInteger, PixelType.Short, 4),
        TextureFormat.Rgba8Unorm => (InternalFormat.Rgba8, PixelFormat.Rgba, PixelType.UnsignedByte, 4),
        TextureFormat.Rgba8UnormSrgb => (InternalFormat.Srgb8Alpha8, PixelFormat.Rgba, PixelType.UnsignedByte, 4),
        TextureFormat.Rgba8Snorm => (InternalFormat.Rgba8SNorm, PixelFormat.Rgba, PixelType.Byte, 4),
        TextureFormat.Rgba8Uint => (InternalFormat.Rgba8ui, PixelFormat.RgbaInteger, PixelType.UnsignedByte, 4),
        TextureFormat.Rgba8Sint => (InternalFormat.Rgba8i, PixelFormat.RgbaInteger, PixelType.Byte, 4),
        TextureFormat.Bgra8Unorm => (InternalFormat.Rgba8, PixelFormat.Bgra, PixelType.UnsignedByte, 4),
        TextureFormat.Bgra8UnormSrgb => (InternalFormat.Srgb8Alpha8, PixelFormat.Bgra, PixelType.UnsignedByte, 4),
        TextureFormat.Rgb10A2Unorm => (InternalFormat.Rgb10A2, PixelFormat.Rgba, PixelType.UnsignedInt2101010Rev, 4),
        TextureFormat.Rg11B10Float => (InternalFormat.R11fG11fB10f, PixelFormat.Rgb, PixelType.UnsignedInt10f11f11fRev, 4),
        TextureFormat.Rg32Float => (InternalFormat.RG32f, PixelFormat.RG, PixelType.Float, 8),
        TextureFormat.Rg32Uint => (InternalFormat.RG32ui, PixelFormat.RGInteger, PixelType.UnsignedInt, 8),
        TextureFormat.Rg32Sint => (InternalFormat.RG32i, PixelFormat.RGInteger, PixelType.Int, 8),
        TextureFormat.Rgba16Float => (InternalFormat.Rgba16f, PixelFormat.Rgba, PixelType.HalfFloat, 8),
        TextureFormat.Rgba16Uint => (InternalFormat.Rgba16ui, PixelFormat.RgbaInteger, PixelType.UnsignedShort, 8),
        TextureFormat.Rgba16Sint => (InternalFormat.Rgba16i, PixelFormat.RgbaInteger, PixelType.Short, 8),
        TextureFormat.Rgba32Float => (InternalFormat.Rgba32f, PixelFormat.Rgba, PixelType.Float, 16),
        TextureFormat.Rgba32Uint => (InternalFormat.Rgba32ui, PixelFormat.RgbaInteger, PixelType.UnsignedInt, 16),
        TextureFormat.Rgba32Sint => (InternalFormat.Rgba32i, PixelFormat.RgbaInteger, PixelType.Int, 16),
        TextureFormat.Depth16Unorm => (InternalFormat.DepthComponent16, PixelFormat.DepthComponent, PixelType.UnsignedShort, 2),
        TextureFormat.Depth24Plus => (InternalFormat.DepthComponent24, PixelFormat.DepthComponent, PixelType.UnsignedInt, 4),
        TextureFormat.Depth24PlusStencil8 => (InternalFormat.Depth24Stencil8, PixelFormat.DepthStencil, PixelType.UnsignedInt248, 4),
        TextureFormat.Depth32Float => (InternalFormat.DepthComponent32f, PixelFormat.DepthComponent, PixelType.Float, 4),
        TextureFormat.Depth32FloatStencil8 => (InternalFormat.Depth32fStencil8, PixelFormat.DepthStencil, PixelType.Float32UnsignedInt248Rev, 5),
        _ => throw new ArgumentOutOfRangeException(nameof(fmt), fmt, null),
    };

    public static int BytesPerPixel(TextureFormat fmt) => ToGl(fmt).Bpp;

    public static TextureTarget ToGlTarget(TextureDimension d) => d switch
    {
        TextureDimension.Tex2D => TextureTarget.Texture2D,
        TextureDimension.Tex2DArray => TextureTarget.Texture2DArray,
        TextureDimension.Tex3D => TextureTarget.Texture3D,
        TextureDimension.Cube => TextureTarget.TextureCubeMap,
        TextureDimension.CubeArray => TextureTarget.TextureCubeMapArray,
        _ => TextureTarget.Texture2D,
    };

    public static TextureMinFilter ToGlMin(FilterMode min, MipmapMode mip) =>
        (min, mip) switch
        {
            (FilterMode.Nearest, MipmapMode.Nearest) => TextureMinFilter.NearestMipmapNearest,
            (FilterMode.Nearest, MipmapMode.Linear) => TextureMinFilter.NearestMipmapLinear,
            (FilterMode.Linear, MipmapMode.Nearest) => TextureMinFilter.LinearMipmapNearest,
            (FilterMode.Linear, MipmapMode.Linear) => TextureMinFilter.LinearMipmapLinear,
            _ => TextureMinFilter.Linear,
        };

    public static TextureMagFilter ToGlMag(FilterMode f) =>
        f == FilterMode.Linear ? TextureMagFilter.Linear : TextureMagFilter.Nearest;

    public static TextureWrapMode ToGlWrap(AddressMode a) => a switch
    {
        AddressMode.ClampToEdge => TextureWrapMode.ClampToEdge,
        AddressMode.Repeat => TextureWrapMode.Repeat,
        AddressMode.MirrorRepeat => TextureWrapMode.MirroredRepeat,
        AddressMode.ClampToBorder => TextureWrapMode.ClampToBorder,
        _ => TextureWrapMode.ClampToEdge,
    };

    public static DepthFunction ToGlDepth(CompareFunc f) => f switch
    {
        CompareFunc.Never => DepthFunction.Never,
        CompareFunc.Less => DepthFunction.Less,
        CompareFunc.Equal => DepthFunction.Equal,
        CompareFunc.LessEqual => DepthFunction.Lequal,
        CompareFunc.Greater => DepthFunction.Greater,
        CompareFunc.NotEqual => DepthFunction.Notequal,
        CompareFunc.GreaterEqual => DepthFunction.Gequal,
        CompareFunc.Always => DepthFunction.Always,
        _ => DepthFunction.Always,
    };

    public static StencilFunction ToGlStencil(CompareFunc f) => f switch
    {
        CompareFunc.Never => StencilFunction.Never,
        CompareFunc.Less => StencilFunction.Less,
        CompareFunc.Equal => StencilFunction.Equal,
        CompareFunc.LessEqual => StencilFunction.Lequal,
        CompareFunc.Greater => StencilFunction.Greater,
        CompareFunc.NotEqual => StencilFunction.Notequal,
        CompareFunc.GreaterEqual => StencilFunction.Gequal,
        CompareFunc.Always => StencilFunction.Always,
        _ => StencilFunction.Always,
    };

    public static GLStencilOp ToGl(Penelope.StencilOp s) => s switch
    {
        Penelope.StencilOp.Keep => GLStencilOp.Keep,
        Penelope.StencilOp.Zero => GLStencilOp.Zero,
        Penelope.StencilOp.Replace => GLStencilOp.Replace,
        Penelope.StencilOp.IncrementClamp => GLStencilOp.Incr,
        Penelope.StencilOp.DecrementClamp => GLStencilOp.Decr,
        Penelope.StencilOp.Invert => GLStencilOp.Invert,
        Penelope.StencilOp.IncrementWrap => GLStencilOp.IncrWrap,
        Penelope.StencilOp.DecrementWrap => GLStencilOp.DecrWrap,
        _ => GLStencilOp.Keep,
    };

    public static BlendingFactor ToGl(BlendFactor f) => f switch
    {
        BlendFactor.Zero => BlendingFactor.Zero,
        BlendFactor.One => BlendingFactor.One,
        BlendFactor.SrcColor => BlendingFactor.SrcColor,
        BlendFactor.OneMinusSrcColor => BlendingFactor.OneMinusSrcColor,
        BlendFactor.SrcAlpha => BlendingFactor.SrcAlpha,
        BlendFactor.OneMinusSrcAlpha => BlendingFactor.OneMinusSrcAlpha,
        BlendFactor.DstColor => BlendingFactor.DstColor,
        BlendFactor.OneMinusDstColor => BlendingFactor.OneMinusDstColor,
        BlendFactor.DstAlpha => BlendingFactor.DstAlpha,
        BlendFactor.OneMinusDstAlpha => BlendingFactor.OneMinusDstAlpha,
        BlendFactor.ConstantColor => BlendingFactor.ConstantColor,
        BlendFactor.OneMinusConstantColor => BlendingFactor.OneMinusConstantColor,
        BlendFactor.ConstantAlpha => BlendingFactor.ConstantAlpha,
        BlendFactor.OneMinusConstantAlpha => BlendingFactor.OneMinusConstantAlpha,
        BlendFactor.SrcAlphaSaturated => BlendingFactor.SrcAlphaSaturate,
        BlendFactor.Src1Color => BlendingFactor.Src1Color,
        BlendFactor.OneMinusSrc1Color => BlendingFactor.OneMinusSrc1Color,
        BlendFactor.Src1Alpha => BlendingFactor.Src1Alpha,
        BlendFactor.OneMinusSrc1Alpha => BlendingFactor.OneMinusSrc1Alpha,
        _ => BlendingFactor.Zero,
    };

    public static GLBlendOp ToGl(BlendOp op) => op switch
    {
        BlendOp.Add => GLBlendOp.FuncAdd,
        BlendOp.Subtract => GLBlendOp.FuncSubtract,
        BlendOp.ReverseSubtract => GLBlendOp.FuncReverseSubtract,
        BlendOp.Min => GLBlendOp.Min,
        BlendOp.Max => GLBlendOp.Max,
        _ => GLBlendOp.FuncAdd,
    };

    public static GLPrimitiveType ToGl(PrimitiveTopology t) => t switch
    {
        PrimitiveTopology.PointList => GLPrimitiveType.Points,
        PrimitiveTopology.LineList => GLPrimitiveType.Lines,
        PrimitiveTopology.LineStrip => GLPrimitiveType.LineStrip,
        PrimitiveTopology.TriangleList => GLPrimitiveType.Triangles,
        PrimitiveTopology.TriangleStrip => GLPrimitiveType.TriangleStrip,
        PrimitiveTopology.TriangleFan => GLPrimitiveType.TriangleFan,
        _ => GLPrimitiveType.Triangles,
    };

    public static (VertexAttribPointerType Type, int Components, bool Normalized, bool IsInteger) ToGl(VertexFormat f) => f switch
    {
        VertexFormat.Float1 => (VertexAttribPointerType.Float, 1, false, false),
        VertexFormat.Float2 => (VertexAttribPointerType.Float, 2, false, false),
        VertexFormat.Float3 => (VertexAttribPointerType.Float, 3, false, false),
        VertexFormat.Float4 => (VertexAttribPointerType.Float, 4, false, false),
        VertexFormat.UByte2 => (VertexAttribPointerType.UnsignedByte, 2, false, true),
        VertexFormat.UByte4 => (VertexAttribPointerType.UnsignedByte, 4, false, true),
        VertexFormat.UByte2Norm => (VertexAttribPointerType.UnsignedByte, 2, true, false),
        VertexFormat.UByte4Norm => (VertexAttribPointerType.UnsignedByte, 4, true, false),
        VertexFormat.Byte2Norm => (VertexAttribPointerType.Byte, 2, true, false),
        VertexFormat.Byte4Norm => (VertexAttribPointerType.Byte, 4, true, false),
        VertexFormat.UShort2 => (VertexAttribPointerType.UnsignedShort, 2, false, true),
        VertexFormat.UShort4 => (VertexAttribPointerType.UnsignedShort, 4, false, true),
        VertexFormat.UShort2Norm => (VertexAttribPointerType.UnsignedShort, 2, true, false),
        VertexFormat.UShort4Norm => (VertexAttribPointerType.UnsignedShort, 4, true, false),
        VertexFormat.Short2Norm => (VertexAttribPointerType.Short, 2, true, false),
        VertexFormat.Short4Norm => (VertexAttribPointerType.Short, 4, true, false),
        VertexFormat.Half2 => (VertexAttribPointerType.HalfFloat, 2, false, false),
        VertexFormat.Half4 => (VertexAttribPointerType.HalfFloat, 4, false, false),
        VertexFormat.UInt1 => (VertexAttribPointerType.UnsignedInt, 1, false, true),
        VertexFormat.UInt2 => (VertexAttribPointerType.UnsignedInt, 2, false, true),
        VertexFormat.UInt3 => (VertexAttribPointerType.UnsignedInt, 3, false, true),
        VertexFormat.UInt4 => (VertexAttribPointerType.UnsignedInt, 4, false, true),
        VertexFormat.Int1 => (VertexAttribPointerType.Int, 1, false, true),
        VertexFormat.Int2 => (VertexAttribPointerType.Int, 2, false, true),
        VertexFormat.Int3 => (VertexAttribPointerType.Int, 3, false, true),
        VertexFormat.Int4 => (VertexAttribPointerType.Int, 4, false, true),
        _ => (VertexAttribPointerType.Float, 0, false, false),
    };

    public static int VertexFormatSize(VertexFormat f)
    {
        var (type, components, _, _) = ToGl(f);
        var typeSize = type switch
        {
            VertexAttribPointerType.Byte or VertexAttribPointerType.UnsignedByte => 1,
            VertexAttribPointerType.Short or VertexAttribPointerType.UnsignedShort or VertexAttribPointerType.HalfFloat => 2,
            _ => 4,
        };
        return typeSize * components;
    }

    public static DrawElementsType ToGl(IndexType t) =>
        t == IndexType.UInt16 ? DrawElementsType.UnsignedShort : DrawElementsType.UnsignedInt;

    public static TriangleFace ToGl(CullMode c) => c switch
    {
        CullMode.Front => TriangleFace.Front,
        CullMode.Back => TriangleFace.Back,
        _ => TriangleFace.Back,
    };

    public static FrontFaceDirection ToGl(FrontFace f) =>
        f == FrontFace.Ccw ? FrontFaceDirection.Ccw : FrontFaceDirection.CW;

    public static GLPolygonMode ToGl(Penelope.PolygonMode p) => p switch
    {
        Penelope.PolygonMode.Line => GLPolygonMode.Line,
        Penelope.PolygonMode.Point => GLPolygonMode.Point,
        _ => GLPolygonMode.Fill,
    };
}
