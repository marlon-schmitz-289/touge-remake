namespace Penelope.Backends.Switch;

/// <summary>
///     Internal Switch (NVN2) resource wrappers. Filled in once the project links against the
///     Nintendo SDK (NDA-gated headers); for now the fields hold opaque <see cref="nint"/>
///     handles so the structure compiles without the SDK headers present.
/// </summary>
internal sealed class SwitchBuffer
{
    public nint NvnBuffer;          // NVNbuffer*
    public nint NvnMemoryPool;      // NVNmemoryPool* — buffer storage lives here
    public int SizeBytes;
    public BufferUsage Usage;
    public BufferAccess Access;
    public nint MappedPtr;          // CPU pointer for shared/host buffers (Switch is unified-memory)
}

internal sealed class SwitchTexture
{
    public nint NvnTexture;         // NVNtexture*
    public nint NvnMemoryPool;
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
    public bool IsSwapchainImage;
}

internal sealed class SwitchTextureView
{
    public ulong TextureId;
    public TextureFormat Format;
    public TextureAspect Aspect;
    public int BaseMip;
    public int MipCount;
    public int BaseLayer;
    public int LayerCount;
    public nint NvnTextureView;     // NVNtextureView* — Switch creates these as full structs, not handles
}

internal sealed class SwitchSampler
{
    public nint NvnSampler;         // NVNsampler*
    public nint NvnSamplerPool;
}

internal sealed class SwitchShader
{
    public nint NvnVertexProgram;   // NVNprogram*
    public nint NvnFragmentProgram;
    public nint NvnComputeProgram;
    public bool HasVertex;
    public bool HasFragment;
    public bool HasCompute;
}

internal sealed class SwitchBindGroupLayout
{
    public BindGroupLayoutEntry[] Entries = [];
}

internal sealed class SwitchBindGroup
{
    public ulong LayoutId;
    public BindGroupEntry[] Entries = [];
}

internal sealed class SwitchRenderPipeline
{
    public nint NvnVertexState;     // NVNvertexAttribState[] / vertexStreamState[] are baked into the bind
    public RenderPipelineDesc Desc;
}

internal sealed class SwitchComputePipeline
{
    public ComputePipelineDesc Desc;
}

internal sealed class SwitchRenderTarget
{
    public ulong ColorTextureId;
    public ulong ColorViewId;
    public ulong DepthTextureId;
    public ulong DepthViewId;
    public RenderTargetDesc Desc;
}

internal sealed class SwitchQueryPool
{
    public nint NvnCounter;         // NVNcounterData*
    public QueryType Type;
    public int Count;
}

internal sealed class SwitchFence
{
    public nint NvnSync;            // NVNsync*
    public bool Signaled;
}
