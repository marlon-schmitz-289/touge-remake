using System.Runtime.Versioning;
using SharpMetal.Metal;

namespace Penelope.Backends.Metal;

/// <summary>Internal Metal resource wrappers. One per Penelope handle id.</summary>
[SupportedOSPlatform("macos")]
internal sealed class MetalBuffer
{
    public MTLBuffer Buffer;
    public int SizeBytes;
    public BufferUsage Usage;
    public BufferAccess Access;
    public bool HostVisible;
}

[SupportedOSPlatform("macos")]
internal sealed class MetalTexture
{
    public MTLTexture Texture;
    public TextureFormat Format;
    public int Width;
    public int Height;
    public int Depth;
    public int MipLevels;
    public int ArrayLayers;
    public int SampleCount;
    public TextureUsage Usage;
    public TextureDimension Dimension;
    public ulong DefaultViewId;
    /// <summary>True for the view backed by the current swapchain drawable. The texture is
    /// re-acquired each frame from CAMetalLayer.NextDrawable.</summary>
    public bool IsSwapchainProxy;
}

[SupportedOSPlatform("macos")]
internal sealed class MetalTextureView
{
    public ulong TextureId;
    public TextureFormat Format;
    public TextureAspect Aspect;
    public int BaseMip;
    public int MipCount;
    public int BaseLayer;
    public int LayerCount;
    /// <summary>Optional separate MTLTexture for non-default views (NewTextureView).</summary>
    public MTLTexture ViewTexture;
    public bool HasViewTexture;
}

[SupportedOSPlatform("macos")]
internal sealed class MetalSampler
{
    public MTLSamplerState Sampler;
}

[SupportedOSPlatform("macos")]
internal sealed class MetalShader
{
    public MTLLibrary VertexLibrary;
    public MTLLibrary FragmentLibrary;
    public MTLLibrary ComputeLibrary;
    public MTLFunction VertexFunction;
    public MTLFunction FragmentFunction;
    public MTLFunction ComputeFunction;
    public bool HasVertex;
    public bool HasFragment;
    public bool HasCompute;
    /// <summary>True if the shader declared a push-constant block — backend uploads via SetVertexBytes/SetFragmentBytes.</summary>
    public bool HasPushConstants;
}

/// <summary>Bind group layout — pure metadata. Metal has no descriptor sets.</summary>
[SupportedOSPlatform("macos")]
internal sealed class MetalBindGroupLayout
{
    public BindGroupLayoutEntry[] Entries = [];
}

[SupportedOSPlatform("macos")]
internal sealed class MetalBindGroup
{
    public ulong LayoutId;
    public BindGroupEntry[] Entries = [];
}

[SupportedOSPlatform("macos")]
internal sealed class MetalRenderPipeline
{
    public MTLRenderPipelineState Pipeline;
    public MTLDepthStencilState DepthStencilState;
    public bool HasDepthStencilState;
    public RenderPipelineDesc Desc;
    public MTLPrimitiveType Topology;
    public MTLCullMode CullMode;
    public MTLWinding Winding;
    public MTLTriangleFillMode FillMode;
    public bool DepthClipEnabled;
    public float DepthBiasConstant;
    public float DepthBiasSlope;
    public float DepthBiasClamp;
}

[SupportedOSPlatform("macos")]
internal sealed class MetalComputePipeline
{
    public MTLComputePipelineState Pipeline;
    public ComputePipelineDesc Desc;
    /// <summary>Threadgroup size from the shader's `[[threads(x,y,z)]]`. Cached for Dispatch.</summary>
    public MTLSize ThreadsPerGroup;
}

[SupportedOSPlatform("macos")]
internal sealed class MetalRenderTarget
{
    public ulong ColorTextureId;
    public ulong ColorViewId;
    public ulong DepthTextureId;
    public ulong DepthViewId;
    public RenderTargetDesc Desc;
}

[SupportedOSPlatform("macos")]
internal sealed class MetalQueryPool
{
    public QueryType Type;
    public int Count;
    /// <summary>Backing buffer for occlusion queries / counters. Not used for timestamps yet.</summary>
    public MTLBuffer Backing;
}

[SupportedOSPlatform("macos")]
internal sealed class MetalFence
{
    public MTLEvent Event;
    public ulong Value;
    public bool Signaled;
}
