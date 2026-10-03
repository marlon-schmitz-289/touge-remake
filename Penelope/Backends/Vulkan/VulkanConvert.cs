using Silk.NET.Vulkan;
using VkStencilOp = Silk.NET.Vulkan.StencilOp;
using VkBlendFactor = Silk.NET.Vulkan.BlendFactor;
using VkBlendOp = Silk.NET.Vulkan.BlendOp;
using VkFrontFace = Silk.NET.Vulkan.FrontFace;
using VkPolygonMode = Silk.NET.Vulkan.PolygonMode;
using VkBorderColor = Silk.NET.Vulkan.BorderColor;
using VkPrimitiveTopology = Silk.NET.Vulkan.PrimitiveTopology;
using VkIndexType = Silk.NET.Vulkan.IndexType;

namespace Penelope.Backends.Vulkan;

internal static class VulkanConvert
{
    public static Format ToVk(TextureFormat fmt) => fmt switch
    {
        TextureFormat.R8Unorm => Format.R8Unorm,
        TextureFormat.R8Snorm => Format.R8SNorm,
        TextureFormat.R8Uint => Format.R8Uint,
        TextureFormat.R8Sint => Format.R8Sint,
        TextureFormat.R16Float => Format.R16Sfloat,
        TextureFormat.R16Uint => Format.R16Uint,
        TextureFormat.R16Sint => Format.R16Sint,
        TextureFormat.Rg8Unorm => Format.R8G8Unorm,
        TextureFormat.Rg8Snorm => Format.R8G8SNorm,
        TextureFormat.R32Float => Format.R32Sfloat,
        TextureFormat.R32Uint => Format.R32Uint,
        TextureFormat.R32Sint => Format.R32Sint,
        TextureFormat.Rg16Float => Format.R16G16Sfloat,
        TextureFormat.Rg16Uint => Format.R16G16Uint,
        TextureFormat.Rg16Sint => Format.R16G16Sint,
        TextureFormat.Rgba8Unorm => Format.R8G8B8A8Unorm,
        TextureFormat.Rgba8UnormSrgb => Format.R8G8B8A8Srgb,
        TextureFormat.Rgba8Snorm => Format.R8G8B8A8SNorm,
        TextureFormat.Rgba8Uint => Format.R8G8B8A8Uint,
        TextureFormat.Rgba8Sint => Format.R8G8B8A8Sint,
        TextureFormat.Bgra8Unorm => Format.B8G8R8A8Unorm,
        TextureFormat.Bgra8UnormSrgb => Format.B8G8R8A8Srgb,
        TextureFormat.Rgb10A2Unorm => Format.A2B10G10R10UnormPack32,
        TextureFormat.Rg11B10Float => Format.B10G11R11UfloatPack32,
        TextureFormat.Rg32Float => Format.R32G32Sfloat,
        TextureFormat.Rg32Uint => Format.R32G32Uint,
        TextureFormat.Rg32Sint => Format.R32G32Sint,
        TextureFormat.Rgba16Float => Format.R16G16B16A16Sfloat,
        TextureFormat.Rgba16Uint => Format.R16G16B16A16Uint,
        TextureFormat.Rgba16Sint => Format.R16G16B16A16Sint,
        TextureFormat.Rgba32Float => Format.R32G32B32A32Sfloat,
        TextureFormat.Rgba32Uint => Format.R32G32B32A32Uint,
        TextureFormat.Rgba32Sint => Format.R32G32B32A32Sint,
        TextureFormat.Depth16Unorm => Format.D16Unorm,
        TextureFormat.Depth24Plus => Format.X8D24UnormPack32,
        TextureFormat.Depth24PlusStencil8 => Format.D24UnormS8Uint,
        TextureFormat.Depth32Float => Format.D32Sfloat,
        TextureFormat.Depth32FloatStencil8 => Format.D32SfloatS8Uint,
        _ => throw new ArgumentOutOfRangeException(nameof(fmt), fmt, null),
    };

    public static TextureFormat FromVk(Format fmt) => fmt switch
    {
        Format.R8Unorm => TextureFormat.R8Unorm,
        Format.R8G8B8A8Unorm => TextureFormat.Rgba8Unorm,
        Format.R8G8B8A8Srgb => TextureFormat.Rgba8UnormSrgb,
        Format.B8G8R8A8Unorm => TextureFormat.Bgra8Unorm,
        Format.B8G8R8A8Srgb => TextureFormat.Bgra8UnormSrgb,
        Format.R16G16B16A16Sfloat => TextureFormat.Rgba16Float,
        Format.R32G32B32A32Sfloat => TextureFormat.Rgba32Float,
        Format.D32Sfloat => TextureFormat.Depth32Float,
        Format.D24UnormS8Uint => TextureFormat.Depth24PlusStencil8,
        _ => throw new ArgumentOutOfRangeException(nameof(fmt), fmt, null),
    };

    public static ImageUsageFlags ToVk(TextureUsage usage)
    {
        ImageUsageFlags f = 0;
        if ((usage & TextureUsage.Sampled) != 0) f |= ImageUsageFlags.SampledBit;
        if ((usage & TextureUsage.Storage) != 0) f |= ImageUsageFlags.StorageBit;
        if ((usage & TextureUsage.ColorAttachment) != 0) f |= ImageUsageFlags.ColorAttachmentBit;
        if ((usage & TextureUsage.DepthStencilAttachment) != 0) f |= ImageUsageFlags.DepthStencilAttachmentBit;
        if ((usage & TextureUsage.CopySrc) != 0) f |= ImageUsageFlags.TransferSrcBit;
        if ((usage & TextureUsage.CopyDst) != 0) f |= ImageUsageFlags.TransferDstBit;
        if ((usage & TextureUsage.TransientAttachment) != 0) f |= ImageUsageFlags.TransientAttachmentBit;
        return f;
    }

    public static BufferUsageFlags ToVk(BufferUsage usage)
    {
        BufferUsageFlags f = 0;
        if ((usage & BufferUsage.Vertex) != 0) f |= BufferUsageFlags.VertexBufferBit;
        if ((usage & BufferUsage.Index) != 0) f |= BufferUsageFlags.IndexBufferBit;
        if ((usage & BufferUsage.Uniform) != 0) f |= BufferUsageFlags.UniformBufferBit;
        if ((usage & BufferUsage.Storage) != 0) f |= BufferUsageFlags.StorageBufferBit;
        if ((usage & BufferUsage.Indirect) != 0) f |= BufferUsageFlags.IndirectBufferBit;
        if ((usage & BufferUsage.CopySrc) != 0) f |= BufferUsageFlags.TransferSrcBit;
        if ((usage & BufferUsage.CopyDst) != 0) f |= BufferUsageFlags.TransferDstBit;
        return f;
    }

    public static ImageViewType ToVkViewType(TextureDimension d) => d switch
    {
        TextureDimension.Tex2D => ImageViewType.Type2D,
        TextureDimension.Tex2DArray => ImageViewType.Type2DArray,
        TextureDimension.Tex3D => ImageViewType.Type3D,
        TextureDimension.Cube => ImageViewType.TypeCube,
        TextureDimension.CubeArray => ImageViewType.TypeCubeArray,
        _ => ImageViewType.Type2D,
    };

    public static ImageType ToVkImageType(TextureDimension d) => d switch
    {
        TextureDimension.Tex3D => ImageType.Type3D,
        _ => ImageType.Type2D,
    };

    public static ImageAspectFlags ToVk(TextureAspect a, Format fmt)
    {
        bool isDepthStencil = fmt is Format.D24UnormS8Uint or Format.D32SfloatS8Uint or Format.D16UnormS8Uint;
        bool isDepth = isDepthStencil || fmt is Format.D16Unorm or Format.D32Sfloat or Format.X8D24UnormPack32;
        bool isStencil = isDepthStencil || fmt is Format.S8Uint;
        return a switch
        {
            TextureAspect.DepthOnly => ImageAspectFlags.DepthBit,
            TextureAspect.StencilOnly => ImageAspectFlags.StencilBit,
            _ => isDepthStencil
                ? ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit
                : isDepth
                    ? ImageAspectFlags.DepthBit
                    : isStencil
                        ? ImageAspectFlags.StencilBit
                        : ImageAspectFlags.ColorBit,
        };
    }

    public static Filter ToVk(FilterMode f) =>
        f == FilterMode.Linear ? Filter.Linear : Filter.Nearest;

    public static SamplerMipmapMode ToVk(MipmapMode m) =>
        m == MipmapMode.Linear ? SamplerMipmapMode.Linear : SamplerMipmapMode.Nearest;

    public static SamplerAddressMode ToVk(AddressMode a) => a switch
    {
        AddressMode.ClampToEdge => SamplerAddressMode.ClampToEdge,
        AddressMode.Repeat => SamplerAddressMode.Repeat,
        AddressMode.MirrorRepeat => SamplerAddressMode.MirroredRepeat,
        AddressMode.ClampToBorder => SamplerAddressMode.ClampToBorder,
        _ => SamplerAddressMode.ClampToEdge,
    };

    public static VkBorderColor ToVk(Penelope.BorderColor b) => b switch
    {
        Penelope.BorderColor.TransparentBlack => VkBorderColor.FloatTransparentBlack,
        Penelope.BorderColor.OpaqueBlack => VkBorderColor.FloatOpaqueBlack,
        Penelope.BorderColor.OpaqueWhite => VkBorderColor.FloatOpaqueWhite,
        _ => VkBorderColor.FloatTransparentBlack,
    };

    public static CompareOp ToVk(CompareFunc f) => f switch
    {
        CompareFunc.Never => CompareOp.Never,
        CompareFunc.Less => CompareOp.Less,
        CompareFunc.Equal => CompareOp.Equal,
        CompareFunc.LessEqual => CompareOp.LessOrEqual,
        CompareFunc.Greater => CompareOp.Greater,
        CompareFunc.NotEqual => CompareOp.NotEqual,
        CompareFunc.GreaterEqual => CompareOp.GreaterOrEqual,
        CompareFunc.Always => CompareOp.Always,
        _ => CompareOp.Always,
    };

    public static VkPrimitiveTopology ToVk(Penelope.PrimitiveTopology t) => t switch
    {
        Penelope.PrimitiveTopology.PointList => VkPrimitiveTopology.PointList,
        Penelope.PrimitiveTopology.LineList => VkPrimitiveTopology.LineList,
        Penelope.PrimitiveTopology.LineStrip => VkPrimitiveTopology.LineStrip,
        Penelope.PrimitiveTopology.TriangleList => VkPrimitiveTopology.TriangleList,
        Penelope.PrimitiveTopology.TriangleStrip => VkPrimitiveTopology.TriangleStrip,
        Penelope.PrimitiveTopology.TriangleFan => VkPrimitiveTopology.TriangleFan,
        _ => VkPrimitiveTopology.TriangleList,
    };

    public static CullModeFlags ToVk(CullMode c) => c switch
    {
        CullMode.Front => CullModeFlags.FrontBit,
        CullMode.Back => CullModeFlags.BackBit,
        _ => CullModeFlags.None,
    };

    public static VkFrontFace ToVk(Penelope.FrontFace f) =>
        f == Penelope.FrontFace.Ccw ? VkFrontFace.CounterClockwise : VkFrontFace.Clockwise;

    public static VkPolygonMode ToVk(Penelope.PolygonMode p) => p switch
    {
        Penelope.PolygonMode.Line => VkPolygonMode.Line,
        Penelope.PolygonMode.Point => VkPolygonMode.Point,
        _ => VkPolygonMode.Fill,
    };

    public static VkBlendFactor ToVk(Penelope.BlendFactor f) => f switch
    {
        Penelope.BlendFactor.Zero => VkBlendFactor.Zero,
        Penelope.BlendFactor.One => VkBlendFactor.One,
        Penelope.BlendFactor.SrcColor => VkBlendFactor.SrcColor,
        Penelope.BlendFactor.OneMinusSrcColor => VkBlendFactor.OneMinusSrcColor,
        Penelope.BlendFactor.SrcAlpha => VkBlendFactor.SrcAlpha,
        Penelope.BlendFactor.OneMinusSrcAlpha => VkBlendFactor.OneMinusSrcAlpha,
        Penelope.BlendFactor.DstColor => VkBlendFactor.DstColor,
        Penelope.BlendFactor.OneMinusDstColor => VkBlendFactor.OneMinusDstColor,
        Penelope.BlendFactor.DstAlpha => VkBlendFactor.DstAlpha,
        Penelope.BlendFactor.OneMinusDstAlpha => VkBlendFactor.OneMinusDstAlpha,
        Penelope.BlendFactor.ConstantColor => VkBlendFactor.ConstantColor,
        Penelope.BlendFactor.OneMinusConstantColor => VkBlendFactor.OneMinusConstantColor,
        Penelope.BlendFactor.ConstantAlpha => VkBlendFactor.ConstantAlpha,
        Penelope.BlendFactor.OneMinusConstantAlpha => VkBlendFactor.OneMinusConstantAlpha,
        Penelope.BlendFactor.SrcAlphaSaturated => VkBlendFactor.SrcAlphaSaturate,
        Penelope.BlendFactor.Src1Color => VkBlendFactor.Src1Color,
        Penelope.BlendFactor.OneMinusSrc1Color => VkBlendFactor.OneMinusSrc1Color,
        Penelope.BlendFactor.Src1Alpha => VkBlendFactor.Src1Alpha,
        Penelope.BlendFactor.OneMinusSrc1Alpha => VkBlendFactor.OneMinusSrc1Alpha,
        _ => VkBlendFactor.Zero,
    };

    public static VkBlendOp ToVk(Penelope.BlendOp op) => op switch
    {
        Penelope.BlendOp.Add => VkBlendOp.Add,
        Penelope.BlendOp.Subtract => VkBlendOp.Subtract,
        Penelope.BlendOp.ReverseSubtract => VkBlendOp.ReverseSubtract,
        Penelope.BlendOp.Min => VkBlendOp.Min,
        Penelope.BlendOp.Max => VkBlendOp.Max,
        _ => VkBlendOp.Add,
    };

    public static ColorComponentFlags ToVk(ColorWriteMask m)
    {
        ColorComponentFlags f = 0;
        if ((m & ColorWriteMask.R) != 0) f |= ColorComponentFlags.RBit;
        if ((m & ColorWriteMask.G) != 0) f |= ColorComponentFlags.GBit;
        if ((m & ColorWriteMask.B) != 0) f |= ColorComponentFlags.BBit;
        if ((m & ColorWriteMask.A) != 0) f |= ColorComponentFlags.ABit;
        return f;
    }

    public static VkStencilOp ToVk(Penelope.StencilOp s) => s switch
    {
        Penelope.StencilOp.Keep => VkStencilOp.Keep,
        Penelope.StencilOp.Zero => VkStencilOp.Zero,
        Penelope.StencilOp.Replace => VkStencilOp.Replace,
        Penelope.StencilOp.IncrementClamp => VkStencilOp.IncrementAndClamp,
        Penelope.StencilOp.DecrementClamp => VkStencilOp.DecrementAndClamp,
        Penelope.StencilOp.Invert => VkStencilOp.Invert,
        Penelope.StencilOp.IncrementWrap => VkStencilOp.IncrementAndWrap,
        Penelope.StencilOp.DecrementWrap => VkStencilOp.DecrementAndWrap,
        _ => VkStencilOp.Keep,
    };

    public static AttachmentLoadOp ToVk(LoadOp op) => op switch
    {
        LoadOp.Load => AttachmentLoadOp.Load,
        LoadOp.Clear => AttachmentLoadOp.Clear,
        LoadOp.DontCare => AttachmentLoadOp.DontCare,
        _ => AttachmentLoadOp.DontCare,
    };

    public static AttachmentStoreOp ToVk(StoreOp op) => op switch
    {
        StoreOp.Store => AttachmentStoreOp.Store,
        StoreOp.DontCare => AttachmentStoreOp.DontCare,
        _ => AttachmentStoreOp.DontCare,
    };

    public static PresentModeKHR ToVk(PresentMode p) => p switch
    {
        PresentMode.Fifo => PresentModeKHR.FifoKhr,
        PresentMode.FifoRelaxed => PresentModeKHR.FifoRelaxedKhr,
        PresentMode.Mailbox => PresentModeKHR.MailboxKhr,
        PresentMode.Immediate => PresentModeKHR.ImmediateKhr,
        _ => PresentModeKHR.FifoKhr,
    };

    public static (Format format, uint size) ToVk(VertexFormat f) => f switch
    {
        VertexFormat.Float1 => (Format.R32Sfloat, 4),
        VertexFormat.Float2 => (Format.R32G32Sfloat, 8),
        VertexFormat.Float3 => (Format.R32G32B32Sfloat, 12),
        VertexFormat.Float4 => (Format.R32G32B32A32Sfloat, 16),
        VertexFormat.UByte2 => (Format.R8G8Uint, 2),
        VertexFormat.UByte4 => (Format.R8G8B8A8Uint, 4),
        VertexFormat.UByte2Norm => (Format.R8G8Unorm, 2),
        VertexFormat.UByte4Norm => (Format.R8G8B8A8Unorm, 4),
        VertexFormat.Byte2Norm => (Format.R8G8SNorm, 2),
        VertexFormat.Byte4Norm => (Format.R8G8B8A8SNorm, 4),
        VertexFormat.UShort2 => (Format.R16G16Uint, 4),
        VertexFormat.UShort4 => (Format.R16G16B16A16Uint, 8),
        VertexFormat.UShort2Norm => (Format.R16G16Unorm, 4),
        VertexFormat.UShort4Norm => (Format.R16G16B16A16Unorm, 8),
        VertexFormat.Short2Norm => (Format.R16G16SNorm, 4),
        VertexFormat.Short4Norm => (Format.R16G16B16A16SNorm, 8),
        VertexFormat.Half2 => (Format.R16G16Sfloat, 4),
        VertexFormat.Half4 => (Format.R16G16B16A16Sfloat, 8),
        VertexFormat.UInt1 => (Format.R32Uint, 4),
        VertexFormat.UInt2 => (Format.R32G32Uint, 8),
        VertexFormat.UInt3 => (Format.R32G32B32Uint, 12),
        VertexFormat.UInt4 => (Format.R32G32B32A32Uint, 16),
        VertexFormat.Int1 => (Format.R32Sint, 4),
        VertexFormat.Int2 => (Format.R32G32Sint, 8),
        VertexFormat.Int3 => (Format.R32G32B32Sint, 12),
        VertexFormat.Int4 => (Format.R32G32B32A32Sint, 16),
        _ => (Format.Undefined, 0),
    };

    public static ShaderStageFlags ToVk(ShaderStage s)
    {
        ShaderStageFlags f = 0;
        if ((s & ShaderStage.Vertex) != 0) f |= ShaderStageFlags.VertexBit;
        if ((s & ShaderStage.Fragment) != 0) f |= ShaderStageFlags.FragmentBit;
        if ((s & ShaderStage.Compute) != 0) f |= ShaderStageFlags.ComputeBit;
        return f;
    }

    public static DescriptorType ToVk(BindingType t) => t switch
    {
        BindingType.UniformBuffer => DescriptorType.UniformBuffer,
        BindingType.StorageBuffer => DescriptorType.StorageBuffer,
        BindingType.ReadOnlyStorageBuffer => DescriptorType.StorageBuffer,
        BindingType.Sampler => DescriptorType.Sampler,
        BindingType.ComparisonSampler => DescriptorType.Sampler,
        BindingType.SampledTexture => DescriptorType.SampledImage,
        BindingType.StorageTexture => DescriptorType.StorageImage,
        BindingType.ReadOnlyStorageTexture => DescriptorType.StorageImage,
        BindingType.CombinedImageSampler => DescriptorType.CombinedImageSampler,
        _ => DescriptorType.UniformBuffer,
    };

    public static SampleCountFlags ToVk(int samples) => samples switch
    {
        1 => SampleCountFlags.Count1Bit,
        2 => SampleCountFlags.Count2Bit,
        4 => SampleCountFlags.Count4Bit,
        8 => SampleCountFlags.Count8Bit,
        16 => SampleCountFlags.Count16Bit,
        _ => SampleCountFlags.Count1Bit,
    };

    public static VkIndexType ToVk(Penelope.IndexType t) =>
        t == Penelope.IndexType.UInt16 ? VkIndexType.Uint16 : VkIndexType.Uint32;
}
