using System.Runtime.Versioning;
using SharpMetal.Metal;
using MTLBlendFactor_ = SharpMetal.Metal.MTLBlendFactor;
using MTLBlendOperation_ = SharpMetal.Metal.MTLBlendOperation;
using MTLStencilOp_ = SharpMetal.Metal.MTLStencilOperation;

namespace Penelope.Backends.Metal;

/// <summary>
///     Penelope ↔ Metal enum/format conversions. Mirrors VulkanConvert / OpenGLConvert.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class MetalConvert
{
    public static MTLPixelFormat ToMetal(TextureFormat fmt) => fmt switch
    {
        TextureFormat.R8Unorm => MTLPixelFormat.R8Unorm,
        TextureFormat.R8Snorm => MTLPixelFormat.R8Snorm,
        TextureFormat.R8Uint => MTLPixelFormat.R8Uint,
        TextureFormat.R8Sint => MTLPixelFormat.R8Sint,
        TextureFormat.R16Float => MTLPixelFormat.R16Float,
        TextureFormat.R16Uint => MTLPixelFormat.R16Uint,
        TextureFormat.R16Sint => MTLPixelFormat.R16Sint,
        TextureFormat.Rg8Unorm => MTLPixelFormat.RG8Unorm,
        TextureFormat.Rg8Snorm => MTLPixelFormat.RG8Snorm,
        TextureFormat.R32Float => MTLPixelFormat.R32Float,
        TextureFormat.R32Uint => MTLPixelFormat.R32Uint,
        TextureFormat.R32Sint => MTLPixelFormat.R32Sint,
        TextureFormat.Rg16Float => MTLPixelFormat.RG16Float,
        TextureFormat.Rg16Uint => MTLPixelFormat.RG16Uint,
        TextureFormat.Rg16Sint => MTLPixelFormat.RG16Sint,
        TextureFormat.Rgba8Unorm => MTLPixelFormat.RGBA8Unorm,
        TextureFormat.Rgba8UnormSrgb => MTLPixelFormat.RGBA8UnormsRGB,
        TextureFormat.Rgba8Snorm => MTLPixelFormat.RGBA8Snorm,
        TextureFormat.Rgba8Uint => MTLPixelFormat.RGBA8Uint,
        TextureFormat.Rgba8Sint => MTLPixelFormat.RGBA8Sint,
        TextureFormat.Bgra8Unorm => MTLPixelFormat.BGRA8Unorm,
        TextureFormat.Bgra8UnormSrgb => MTLPixelFormat.BGRA8UnormsRGB,
        TextureFormat.Rgb10A2Unorm => MTLPixelFormat.RGB10A2Unorm,
        TextureFormat.Rg11B10Float => MTLPixelFormat.RG11B10Float,
        TextureFormat.Rg32Float => MTLPixelFormat.RG32Float,
        TextureFormat.Rg32Uint => MTLPixelFormat.RG32Uint,
        TextureFormat.Rg32Sint => MTLPixelFormat.RG32Sint,
        TextureFormat.Rgba16Float => MTLPixelFormat.RGBA16Float,
        TextureFormat.Rgba16Uint => MTLPixelFormat.RGBA16Uint,
        TextureFormat.Rgba16Sint => MTLPixelFormat.RGBA16Sint,
        TextureFormat.Rgba32Float => MTLPixelFormat.RGBA32Float,
        TextureFormat.Rgba32Uint => MTLPixelFormat.RGBA32Uint,
        TextureFormat.Rgba32Sint => MTLPixelFormat.RGBA32Sint,
        TextureFormat.Depth16Unorm => MTLPixelFormat.Depth16Unorm,
        // Apple silicon doesn't support D24X8 or D24S8 as pixel formats — fall back to D32F.
        TextureFormat.Depth24Plus => MTLPixelFormat.Depth32Float,
        TextureFormat.Depth24PlusStencil8 => MTLPixelFormat.Depth32FloatStencil8,
        TextureFormat.Depth32Float => MTLPixelFormat.Depth32Float,
        TextureFormat.Depth32FloatStencil8 => MTLPixelFormat.Depth32FloatStencil8,
        _ => throw new ArgumentOutOfRangeException(nameof(fmt), fmt, null),
    };

    /// <summary>Bytes per pixel (uncompressed formats only — compressed needs block math).</summary>
    public static int BytesPerPixel(TextureFormat fmt) => fmt switch
    {
        TextureFormat.R8Unorm or TextureFormat.R8Snorm or TextureFormat.R8Uint or TextureFormat.R8Sint => 1,
        TextureFormat.R16Float or TextureFormat.R16Uint or TextureFormat.R16Sint
            or TextureFormat.Rg8Unorm or TextureFormat.Rg8Snorm or TextureFormat.Depth16Unorm => 2,
        TextureFormat.Rgba8Unorm or TextureFormat.Rgba8UnormSrgb or TextureFormat.Rgba8Snorm
            or TextureFormat.Rgba8Uint or TextureFormat.Rgba8Sint
            or TextureFormat.Bgra8Unorm or TextureFormat.Bgra8UnormSrgb
            or TextureFormat.Rgb10A2Unorm or TextureFormat.Rg11B10Float
            or TextureFormat.R32Float or TextureFormat.R32Uint or TextureFormat.R32Sint
            or TextureFormat.Rg16Float or TextureFormat.Rg16Uint or TextureFormat.Rg16Sint
            or TextureFormat.Depth24Plus or TextureFormat.Depth32Float => 4,
        TextureFormat.Depth24PlusStencil8 => 4,
        TextureFormat.Depth32FloatStencil8 => 5,
        TextureFormat.Rg32Float or TextureFormat.Rg32Uint or TextureFormat.Rg32Sint
            or TextureFormat.Rgba16Float or TextureFormat.Rgba16Uint or TextureFormat.Rgba16Sint => 8,
        TextureFormat.Rgba32Float or TextureFormat.Rgba32Uint or TextureFormat.Rgba32Sint => 16,
        _ => 4,
    };

    public static MTLTextureType ToMetal(TextureDimension d) => d switch
    {
        TextureDimension.Tex2D => MTLTextureType.Type2D,
        TextureDimension.Tex2DArray => MTLTextureType.Type2DArray,
        TextureDimension.Tex3D => MTLTextureType.Type3D,
        TextureDimension.Cube => MTLTextureType.Cube,
        TextureDimension.CubeArray => MTLTextureType.CubeArray,
        _ => MTLTextureType.Type2D,
    };

    public static MTLTextureUsage ToMetal(TextureUsage usage)
    {
        MTLTextureUsage f = 0;
        if ((usage & TextureUsage.Sampled) != 0) f |= MTLTextureUsage.ShaderRead;
        if ((usage & TextureUsage.Storage) != 0) f |= MTLTextureUsage.ShaderRead | MTLTextureUsage.ShaderWrite;
        if ((usage & TextureUsage.ColorAttachment) != 0) f |= MTLTextureUsage.RenderTarget;
        if ((usage & TextureUsage.DepthStencilAttachment) != 0) f |= MTLTextureUsage.RenderTarget;
        // CopySrc / CopyDst don't have direct Metal equivalents — copies on textures with
        // ShaderRead always work, and RenderTarget covers writes from blits/resolves.
        return f;
    }

    public static MTLSamplerAddressMode ToMetal(AddressMode a) => a switch
    {
        AddressMode.ClampToEdge => MTLSamplerAddressMode.ClampToEdge,
        AddressMode.Repeat => MTLSamplerAddressMode.Repeat,
        AddressMode.MirrorRepeat => MTLSamplerAddressMode.MirrorRepeat,
        AddressMode.ClampToBorder => MTLSamplerAddressMode.ClampToBorderColor,
        _ => MTLSamplerAddressMode.ClampToEdge,
    };

    public static MTLSamplerMinMagFilter ToMetal(FilterMode f) =>
        f == FilterMode.Linear ? MTLSamplerMinMagFilter.Linear : MTLSamplerMinMagFilter.Nearest;

    public static MTLSamplerMipFilter ToMetalMip(MipmapMode m) =>
        m == MipmapMode.Linear ? MTLSamplerMipFilter.Linear : MTLSamplerMipFilter.Nearest;

    public static MTLSamplerBorderColor ToMetal(BorderColor c) => c switch
    {
        BorderColor.OpaqueBlack => MTLSamplerBorderColor.OpaqueBlack,
        BorderColor.OpaqueWhite => MTLSamplerBorderColor.OpaqueWhite,
        _ => MTLSamplerBorderColor.TransparentBlack,
    };

    public static MTLCompareFunction ToMetal(CompareFunc f) => f switch
    {
        CompareFunc.Never => MTLCompareFunction.Never,
        CompareFunc.Less => MTLCompareFunction.Less,
        CompareFunc.Equal => MTLCompareFunction.Equal,
        CompareFunc.LessEqual => MTLCompareFunction.LessEqual,
        CompareFunc.Greater => MTLCompareFunction.Greater,
        CompareFunc.NotEqual => MTLCompareFunction.NotEqual,
        CompareFunc.GreaterEqual => MTLCompareFunction.GreaterEqual,
        CompareFunc.Always => MTLCompareFunction.Always,
        _ => MTLCompareFunction.Always,
    };

    public static MTLPrimitiveType ToMetal(PrimitiveTopology t) => t switch
    {
        PrimitiveTopology.PointList => MTLPrimitiveType.Point,
        PrimitiveTopology.LineList => MTLPrimitiveType.Line,
        PrimitiveTopology.LineStrip => MTLPrimitiveType.LineStrip,
        PrimitiveTopology.TriangleList => MTLPrimitiveType.Triangle,
        PrimitiveTopology.TriangleStrip => MTLPrimitiveType.TriangleStrip,
        // Metal does not support TriangleFan natively (deprecated even on GL/Vulkan).
        PrimitiveTopology.TriangleFan => MTLPrimitiveType.Triangle,
        _ => MTLPrimitiveType.Triangle,
    };

    public static MTLCullMode ToMetal(CullMode c) => c switch
    {
        CullMode.Front => MTLCullMode.Front,
        CullMode.Back => MTLCullMode.Back,
        _ => MTLCullMode.None,
    };

    public static MTLWinding ToMetal(FrontFace f) =>
        f == FrontFace.Ccw ? MTLWinding.CounterClockwise : MTLWinding.Clockwise;

    public static MTLTriangleFillMode ToMetal(PolygonMode p) => p switch
    {
        PolygonMode.Line => MTLTriangleFillMode.Lines,
        // Metal has no point-fill mode; fall back to lines so the silhouette is still visible.
        PolygonMode.Point => MTLTriangleFillMode.Lines,
        _ => MTLTriangleFillMode.Fill,
    };

    public static MTLBlendFactor_ ToMetal(BlendFactor f) => f switch
    {
        BlendFactor.Zero => MTLBlendFactor_.Zero,
        BlendFactor.One => MTLBlendFactor_.One,
        BlendFactor.SrcColor => MTLBlendFactor_.SourceColor,
        BlendFactor.OneMinusSrcColor => MTLBlendFactor_.OneMinusSourceColor,
        BlendFactor.SrcAlpha => MTLBlendFactor_.SourceAlpha,
        BlendFactor.OneMinusSrcAlpha => MTLBlendFactor_.OneMinusSourceAlpha,
        BlendFactor.DstColor => MTLBlendFactor_.DestinationColor,
        BlendFactor.OneMinusDstColor => MTLBlendFactor_.OneMinusDestinationColor,
        BlendFactor.DstAlpha => MTLBlendFactor_.DestinationAlpha,
        BlendFactor.OneMinusDstAlpha => MTLBlendFactor_.OneMinusDestinationAlpha,
        BlendFactor.ConstantColor => MTLBlendFactor_.BlendColor,
        BlendFactor.OneMinusConstantColor => MTLBlendFactor_.OneMinusBlendColor,
        BlendFactor.ConstantAlpha => MTLBlendFactor_.BlendAlpha,
        BlendFactor.OneMinusConstantAlpha => MTLBlendFactor_.OneMinusBlendAlpha,
        BlendFactor.SrcAlphaSaturated => MTLBlendFactor_.SourceAlphaSaturated,
        BlendFactor.Src1Color => MTLBlendFactor_.Source1Color,
        BlendFactor.OneMinusSrc1Color => MTLBlendFactor_.OneMinusSource1Color,
        BlendFactor.Src1Alpha => MTLBlendFactor_.Source1Alpha,
        BlendFactor.OneMinusSrc1Alpha => MTLBlendFactor_.OneMinusSource1Alpha,
        _ => MTLBlendFactor_.Zero,
    };

    public static MTLBlendOperation_ ToMetal(BlendOp op) => op switch
    {
        BlendOp.Add => MTLBlendOperation_.Add,
        BlendOp.Subtract => MTLBlendOperation_.Subtract,
        BlendOp.ReverseSubtract => MTLBlendOperation_.ReverseSubtract,
        BlendOp.Min => MTLBlendOperation_.Min,
        BlendOp.Max => MTLBlendOperation_.Max,
        _ => MTLBlendOperation_.Add,
    };

    public static MTLColorWriteMask ToMetal(ColorWriteMask m)
    {
        MTLColorWriteMask f = 0;
        if ((m & ColorWriteMask.R) != 0) f |= MTLColorWriteMask.Red;
        if ((m & ColorWriteMask.G) != 0) f |= MTLColorWriteMask.Green;
        if ((m & ColorWriteMask.B) != 0) f |= MTLColorWriteMask.Blue;
        if ((m & ColorWriteMask.A) != 0) f |= MTLColorWriteMask.Alpha;
        return f;
    }

    public static MTLStencilOp_ ToMetal(StencilOp s) => s switch
    {
        StencilOp.Keep => MTLStencilOp_.Keep,
        StencilOp.Zero => MTLStencilOp_.Zero,
        StencilOp.Replace => MTLStencilOp_.Replace,
        StencilOp.IncrementClamp => MTLStencilOp_.IncrementClamp,
        StencilOp.DecrementClamp => MTLStencilOp_.DecrementClamp,
        StencilOp.Invert => MTLStencilOp_.Invert,
        StencilOp.IncrementWrap => MTLStencilOp_.IncrementWrap,
        StencilOp.DecrementWrap => MTLStencilOp_.DecrementWrap,
        _ => MTLStencilOp_.Keep,
    };

    public static MTLLoadAction ToMetal(LoadOp op) => op switch
    {
        LoadOp.Load => MTLLoadAction.Load,
        LoadOp.Clear => MTLLoadAction.Clear,
        LoadOp.DontCare => MTLLoadAction.DontCare,
        _ => MTLLoadAction.DontCare,
    };

    public static MTLStoreAction ToMetal(StoreOp op) => op switch
    {
        StoreOp.Store => MTLStoreAction.Store,
        StoreOp.DontCare => MTLStoreAction.DontCare,
        _ => MTLStoreAction.DontCare,
    };

    public static MTLIndexType ToMetal(IndexType t) =>
        t == IndexType.UInt16 ? MTLIndexType.UInt16 : MTLIndexType.UInt32;

    public static MTLVertexFormat ToMetal(VertexFormat f) => f switch
    {
        VertexFormat.Float1 => MTLVertexFormat.Float,
        VertexFormat.Float2 => MTLVertexFormat.Float2,
        VertexFormat.Float3 => MTLVertexFormat.Float3,
        VertexFormat.Float4 => MTLVertexFormat.Float4,
        VertexFormat.UByte2 => MTLVertexFormat.UChar2,
        VertexFormat.UByte4 => MTLVertexFormat.UChar4,
        VertexFormat.UByte2Norm => MTLVertexFormat.UChar2Normalized,
        VertexFormat.UByte4Norm => MTLVertexFormat.UChar4Normalized,
        VertexFormat.Byte2Norm => MTLVertexFormat.Char2Normalized,
        VertexFormat.Byte4Norm => MTLVertexFormat.Char4Normalized,
        VertexFormat.UShort2 => MTLVertexFormat.UShort2,
        VertexFormat.UShort4 => MTLVertexFormat.UShort4,
        VertexFormat.UShort2Norm => MTLVertexFormat.UShort2Normalized,
        VertexFormat.UShort4Norm => MTLVertexFormat.UShort4Normalized,
        VertexFormat.Short2Norm => MTLVertexFormat.Short2Normalized,
        VertexFormat.Short4Norm => MTLVertexFormat.Short4Normalized,
        VertexFormat.Half2 => MTLVertexFormat.Half2,
        VertexFormat.Half4 => MTLVertexFormat.Half4,
        VertexFormat.UInt1 => MTLVertexFormat.UInt,
        VertexFormat.UInt2 => MTLVertexFormat.UInt2,
        VertexFormat.UInt3 => MTLVertexFormat.UInt3,
        VertexFormat.UInt4 => MTLVertexFormat.UInt4,
        VertexFormat.Int1 => MTLVertexFormat.Int,
        VertexFormat.Int2 => MTLVertexFormat.Int2,
        VertexFormat.Int3 => MTLVertexFormat.Int3,
        VertexFormat.Int4 => MTLVertexFormat.Int4,
        _ => MTLVertexFormat.Invalid,
    };

    public static int VertexFormatSize(VertexFormat f) => f switch
    {
        VertexFormat.Float1 or VertexFormat.UInt1 or VertexFormat.Int1 => 4,
        VertexFormat.Float2 or VertexFormat.UInt2 or VertexFormat.Int2
            or VertexFormat.Half4 or VertexFormat.UShort4 or VertexFormat.UShort4Norm
            or VertexFormat.Short4Norm => 8,
        VertexFormat.Float3 or VertexFormat.UInt3 or VertexFormat.Int3 => 12,
        VertexFormat.Float4 or VertexFormat.UInt4 or VertexFormat.Int4 => 16,
        VertexFormat.UByte2 or VertexFormat.UByte2Norm or VertexFormat.Byte2Norm
            or VertexFormat.Half2 or VertexFormat.UShort2 or VertexFormat.UShort2Norm
            or VertexFormat.Short2Norm => 4,
        VertexFormat.UByte4 or VertexFormat.UByte4Norm or VertexFormat.Byte4Norm => 4,
        _ => 0,
    };
}
