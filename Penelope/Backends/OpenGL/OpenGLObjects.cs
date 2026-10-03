using Silk.NET.OpenGL;

namespace Penelope.Backends.OpenGL;

/// <summary>Internal OpenGL resource wrappers. One per Penelope handle id.</summary>
internal sealed class OpenGLBuffer
{
    public uint Buffer;
    public int SizeBytes;
    public BufferUsage Usage;
    public BufferAccess Access;
    /// <summary>Persistently-mapped pointer for host-visible buffers (GL 4.4+ ARB_buffer_storage).</summary>
    public unsafe void* MappedPtr;
    /// <summary>True if backed by glBufferStorage (immutable storage); false for glBufferData (mutable).</summary>
    public bool ImmutableStorage;
}

internal sealed class OpenGLTexture
{
    public uint Texture;
    public TextureTarget Target;
    public TextureFormat Format;
    public int Width;
    public int Height;
    public int Depth;
    public int MipLevels;
    public int ArrayLayers;
    public int SampleCount;
    public TextureUsage Usage;
    public TextureDimension Dimension;
    /// <summary>Default full-range view id (registered alongside the texture).</summary>
    public ulong DefaultViewId;
    /// <summary>True if this is the synthetic swapchain texture wrapping the default framebuffer.</summary>
    public bool IsSwapchainProxy;
}

internal sealed class OpenGLTextureView
{
    public ulong TextureId;
    public TextureFormat Format;
    public TextureAspect Aspect;
    public int BaseMip;
    public int MipCount;
    public int BaseLayer;
    public int LayerCount;
    /// <summary>Optional: an actual GL texture view created via glTextureView for non-default views.</summary>
    public uint ViewTexture; // 0 if alias of underlying texture
}

internal sealed class OpenGLSampler
{
    public uint Sampler;
}

internal sealed class OpenGLShader
{
    public uint Program;
    public bool HasVertex;
    public bool HasFragment;
    public bool HasCompute;
    /// <summary>
    ///     If true, the source shader declared a push-constant block; the OpenGL backend will bind
    ///     the emulating UBO at <see cref="ShaderLib.PushConstantBinding"/> for every draw.
    /// </summary>
    public bool HasPushConstants;
    /// <summary>
    ///     Block-name SPIRV-Cross gave the rewritten push-constant UBO. We resolve it via
    ///     glGetUniformBlockIndex + glUniformBlockBinding so the application-side
    ///     <see cref="ShaderLib.PushConstantBinding"/> slot matches the shader's binding.
    /// </summary>
    public string? PushConstantBlockName;
}

/// <summary>Bind group layout — pure metadata. GL doesn't have descriptor sets.</summary>
internal sealed class OpenGLBindGroupLayout
{
    public BindGroupLayoutEntry[] Entries = [];
}

internal sealed class OpenGLBindGroup
{
    public ulong LayoutId;
    public BindGroupEntry[] Entries = [];
}

internal sealed class OpenGLRenderPipeline
{
    public uint Program;
    public uint Vao;
    public RenderPipelineDesc Desc;
    public PrimitiveType Topology;
    public bool HasPushConstants;
}

internal sealed class OpenGLComputePipeline
{
    public uint Program;
    public ComputePipelineDesc Desc;
    public bool HasPushConstants;
}

internal sealed class OpenGLRenderTarget
{
    public ulong ColorTextureId;
    public ulong ColorViewId;
    public ulong DepthTextureId;
    public ulong DepthViewId;
    public RenderTargetDesc Desc;
}

internal sealed class OpenGLQueryPool
{
    public uint[] Queries = [];
    public QueryType Type;
}

internal sealed class OpenGLFence
{
    /// <summary>GL sync object handle (nint, opaque).</summary>
    public nint Sync;
    public bool Signaled;
}
