using Silk.NET.Vulkan;

namespace Penelope.Backends.Vulkan;

/// <summary>Internal Vulkan resource wrappers. One per Penelope handle id.</summary>
internal sealed class VulkanBuffer
{
    public Silk.NET.Vulkan.Buffer Buffer;
    public DeviceMemory Memory;
    public ulong Size;
    public BufferUsage Usage;
    public BufferAccess Access;
    public bool HostVisible;
    public unsafe void* MappedPtr;
}

internal sealed class VulkanImage
{
    public Image Image;
    public DeviceMemory Memory;
    public Format Format;
    public Extent3D Extent;
    public uint MipLevels;
    public uint ArrayLayers;
    public SampleCountFlags Samples;
    public TextureUsage Usage;
    public TextureDimension Dimension;
    /// <summary>Default full-range view. Created eagerly.</summary>
    public ulong DefaultViewId;
    public ImageLayout CurrentLayout;
    /// <summary>True if backed by swapchain (image owned by swapchain, do not destroy).</summary>
    public bool IsSwapchainImage;
}

internal sealed class VulkanImageView
{
    public ImageView View;
    public ulong ImageId;
    public Format Format;
    public ImageAspectFlags Aspect;
    public uint BaseMip;
    public uint MipCount;
    public uint BaseLayer;
    public uint LayerCount;
}

internal sealed class VulkanSampler
{
    public Sampler Sampler;
}

internal sealed class VulkanShader
{
    public ShaderModule VertexModule;
    public ShaderModule FragmentModule;
    public ShaderModule ComputeModule;
    public string VertexEntry = "main";
    public string FragmentEntry = "main";
    public string ComputeEntry = "main";
    public bool HasVertex;
    public bool HasFragment;
    public bool HasCompute;
}

internal sealed class VulkanBindGroupLayout
{
    public DescriptorSetLayout Layout;
    public BindGroupLayoutEntry[] Entries = [];
}

internal sealed class VulkanBindGroup
{
    public DescriptorSet Set;
    public ulong LayoutId;
}

internal sealed class VulkanRenderPipeline
{
    public Pipeline Pipeline;
    public PipelineLayout Layout;
    public ulong[] BindGroupLayoutIds = [];
    public PushConstantRange[] PushConstants = [];
    public RenderPipelineDesc Desc;
}

internal sealed class VulkanComputePipeline
{
    public Pipeline Pipeline;
    public PipelineLayout Layout;
    public ulong[] BindGroupLayoutIds = [];
    public PushConstantRange[] PushConstants = [];
}

internal sealed class VulkanRenderTarget
{
    public ulong ColorImageId;
    public ulong ColorViewId;
    public ulong DepthImageId;
    public ulong DepthViewId;
    public RenderTargetDesc Desc;
}

internal sealed class VulkanQueryPool
{
    public QueryPool Pool;
    public QueryType Type;
    public int Count;
}

internal sealed class VulkanFence
{
    public Silk.NET.Vulkan.Fence Fence;
    /// <summary>Once recycled back into the free list, ignore signal checks.</summary>
    public bool Recycled;
}
