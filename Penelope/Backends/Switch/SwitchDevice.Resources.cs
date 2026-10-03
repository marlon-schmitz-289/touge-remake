namespace Penelope.Backends.Switch;

public sealed unsafe partial class SwitchDevice
{
    // ---- Buffers ----

    public BufferHandle CreateBuffer(in BufferDesc desc, ReadOnlySpan<byte> initialData = default)
        => throw NvnNotImplemented(nameof(CreateBuffer),
            "Allocate from a NVNmemoryPool with the right flags (CPU_UNCACHED for shared, " +
            "GPU_CACHED for device-local), then nvnBufferBuilderSetStorage + nvnBufferInitialize. " +
            "For initialData, memcpy through the mapped pointer (Switch is unified-memory).");

    public void WriteBuffer(BufferHandle buffer, int offsetBytes, ReadOnlySpan<byte> data)
        => throw NvnNotImplemented(nameof(WriteBuffer),
            "memcpy into the buffer's mapped pointer (NVNbuffer.GetMappedPtr).");

    public Span<byte> MapBuffer(BufferHandle buffer, int offsetBytes, int sizeBytes)
        => throw NvnNotImplemented(nameof(MapBuffer),
            "Return a Span over NVNbuffer.GetMappedPtr — Switch buffers are persistently mapped.");

    public void UnmapBuffer(BufferHandle buffer)
        => throw NvnNotImplemented(nameof(UnmapBuffer),
            "No-op — Switch persistent mapping matches the Vulkan/Metal contract.");

    public void DestroyBuffer(BufferHandle buffer)
        => throw NvnNotImplemented(nameof(DestroyBuffer),
            "nvnBufferFinalize then return its memory range to the pool.");

    // ---- Textures ----

    public TextureHandle CreateTexture(in TextureDesc desc, ReadOnlySpan<byte> initialData = default)
        => throw NvnNotImplemented(nameof(CreateTexture),
            "nvnTextureBuilderSet* + nvnTextureInitialize against an NVNmemoryPool sized via " +
            "nvnTextureBuilderGetStorageSize. For initialData, blit through a staging buffer + " +
            "nvnCommandBufferCopyBufferToTexture on a transient queue.");

    public void WriteTexture(
        TextureHandle texture,
        int mipLevel, int arrayLayer,
        int x, int y, int z,
        int width, int height, int depth,
        ReadOnlySpan<byte> data,
        int bytesPerRow, int rowsPerImage)
        => throw NvnNotImplemented(nameof(WriteTexture),
            "Stage data into a shared NVNbuffer + nvnCommandBufferCopyBufferToTexture with a " +
            "NVNcopyRegion describing (x,y,z,w,h,d).");

    public void DestroyTexture(TextureHandle texture)
        => throw NvnNotImplemented(nameof(DestroyTexture),
            "nvnTextureFinalize.");

    public TextureViewHandle DefaultTextureView(TextureHandle texture)
        => throw NvnNotImplemented(nameof(DefaultTextureView),
            "Return the cached default-view id created at CreateTexture time.");

    public TextureViewHandle CreateTextureView(TextureHandle texture, in TextureViewDesc desc)
        => throw NvnNotImplemented(nameof(CreateTextureView),
            "Build NVNtextureView with nvnTextureViewSetLevels/Layers/Format and store as a Penelope view.");

    public void DestroyTextureView(TextureViewHandle view)
        => throw NvnNotImplemented(nameof(DestroyTextureView),
            "Drop the view from the registry; NVNtextureView is plain data, no finalize needed.");

    // ---- Samplers ----

    public SamplerHandle CreateSampler(in SamplerDesc desc)
        => throw NvnNotImplemented(nameof(CreateSampler),
            "nvnSamplerBuilderSet* + nvnSamplerInitialize against an NVNsamplerPool.");

    public void DestroySampler(SamplerHandle sampler)
        => throw NvnNotImplemented(nameof(DestroySampler),
            "nvnSamplerFinalize.");

    public SamplerHandle GetSampler(in SamplerDesc desc)
        => throw NvnNotImplemented(nameof(GetSampler),
            "Cache by SamplerDesc (with DebugName normalized) → CreateSampler. Same shape as " +
            "the desktop backends; once CreateSampler lands the cache code is identical.");

    // ---- Render targets ----

    public RenderTargetHandle CreateRenderTarget(in RenderTargetDesc desc)
        => throw NvnNotImplemented(nameof(CreateRenderTarget),
            "Color = CreateTexture(ColorAttachment+Sampled), Depth = CreateTexture(DepthStencilAttachment).");

    public void DestroyRenderTarget(RenderTargetHandle rt)
        => throw NvnNotImplemented(nameof(DestroyRenderTarget),
            "DestroyTexture for both color + depth.");

    public TextureViewHandle GetRenderTargetColorView(RenderTargetHandle rt)
        => throw NvnNotImplemented(nameof(GetRenderTargetColorView), "Return cached view.");

    public TextureViewHandle GetRenderTargetDepthView(RenderTargetHandle rt)
        => throw NvnNotImplemented(nameof(GetRenderTargetDepthView), "Return cached view.");

    // ---- Query pools ----

    public QueryPoolHandle CreateQueryPool(in QueryPoolDesc desc)
        => throw NvnNotImplemented(nameof(CreateQueryPool),
            "Allocate a NVNcounterData buffer sized for desc.Count entries (8 or 16 bytes each).");

    public void DestroyQueryPool(QueryPoolHandle pool)
        => throw NvnNotImplemented(nameof(DestroyQueryPool), "Free the counter buffer.");

    public void ReadQueryPool(QueryPoolHandle pool, int firstQuery, Span<ulong> results)
        => throw NvnNotImplemented(nameof(ReadQueryPool),
            "Wait on the queue + memcpy from the counter buffer's mapped pointer into results.");

    // ---- Fences ----

    public bool WaitForFence(FenceHandle fence, TimeSpan timeout)
        => throw NvnNotImplemented(nameof(WaitForFence),
            "nvnSyncWait(sync, timeoutNs) returning the appropriate WAIT_RESULT code.");

    public bool IsFenceSignaled(FenceHandle fence)
        => throw NvnNotImplemented(nameof(IsFenceSignaled),
            "nvnSyncWait with timeout=0 — ALREADY_SIGNALED → true.");

    public void DestroyFence(FenceHandle fence)
        => throw NvnNotImplemented(nameof(DestroyFence), "nvnSyncFinalize.");
}
