namespace Penelope;

/// <summary>
///     Graphics device. Owns all GPU resources and the swapchain. Created once per window;
///     disposal tears down every resource the device minted. Thread-safety is per-backend — treat
///     the device as single-threaded unless a backend documents otherwise.
/// </summary>
public interface IPenelopeDevice : IDisposable
{
    BackendKind Backend { get; }
    AdapterInfo Adapter { get; }
    DeviceFeatures Features { get; }
    DeviceLimits Limits { get; }

    // ---- Frame slot model ----

    /// <summary>
    ///     Number of independent in-flight frame slots the backend cycles through. Each slot
    ///     owns its own command resources (and any caller-side per-frame buffers, e.g.
    ///     <see cref="TransientBufferRing"/>). <see cref="BeginFrame"/> waits until the slot
    ///     it is about to use has been retired by the GPU before returning, so the slot's
    ///     resources are safe for the CPU to overwrite.
    /// </summary>
    int FramesInFlight { get; }

    /// <summary>
    ///     Slot index in <c>[0, FramesInFlight)</c> the device is currently recording into.
    ///     Stable for the duration of a frame; advances after <see cref="EndFrame"/>.
    ///     Per-frame allocators key off this to rotate which sub-region they hand out.
    /// </summary>
    int CurrentFrameSlot { get; }

    // ---- Swapchain ----

    int SwapchainWidth { get; }
    int SwapchainHeight { get; }
    TextureFormat SwapchainFormat { get; }
    PresentMode PresentMode { get; }

    void ConfigureSwapchain(in SwapchainDesc desc);
    void ResizeSwapchain(int width, int height);

    /// <summary>
    ///     Toggle vsync by re-configuring the swapchain with a new present mode. Internally
    ///     calls <c>WaitIdle</c> + Configure, so don't call inside a render pass.
    /// </summary>
    void SetVSync(bool vsync);

    /// <summary>
    ///     View of the current swapchain image. Only valid between <see cref="BeginFrame"/> and
    ///     <see cref="EndFrame"/>. Use as a color attachment in a render pass.
    /// </summary>
    TextureViewHandle CurrentSwapchainView { get; }

    // ---- Buffers ----

    BufferHandle CreateBuffer(in BufferDesc desc, ReadOnlySpan<byte> initialData = default);
    void WriteBuffer(BufferHandle buffer, int offsetBytes, ReadOnlySpan<byte> data);
    void DestroyBuffer(BufferHandle buffer);

    /// <summary>
    ///     CPU-visible map of a MapRead or MapWrite buffer. Call <see cref="UnmapBuffer"/> after
    ///     use. Asserts that the buffer was created with a Map* usage.
    /// </summary>
    unsafe Span<byte> MapBuffer(BufferHandle buffer, int offsetBytes, int sizeBytes);
    void UnmapBuffer(BufferHandle buffer);

    // ---- Textures & views ----

    TextureHandle CreateTexture(in TextureDesc desc, ReadOnlySpan<byte> initialData = default);
    void WriteTexture(
        TextureHandle texture,
        int mipLevel,
        int arrayLayer,
        int x, int y, int z,
        int width, int height, int depth,
        ReadOnlySpan<byte> data,
        int bytesPerRow,
        int rowsPerImage);
    void DestroyTexture(TextureHandle texture);

    /// <summary>Default view of a texture — all mips, all layers, matching format.</summary>
    TextureViewHandle DefaultTextureView(TextureHandle texture);

    TextureViewHandle CreateTextureView(TextureHandle texture, in TextureViewDesc desc);
    void DestroyTextureView(TextureViewHandle view);

    // ---- Samplers ----

    SamplerHandle CreateSampler(in SamplerDesc desc);
    void DestroySampler(SamplerHandle sampler);

    /// <summary>
    ///     Cached sampler accessor. Returns a shared sampler for the given desc — repeated
    ///     calls with the same parameters return the same handle. The device owns the
    ///     lifetime: cached samplers are destroyed when the device is disposed. Prefer this
    ///     over <see cref="CreateSampler"/> for the well-known <c>SamplerDesc.Nearest</c> /
    ///     <c>Linear</c> / <c>LinearWrap</c> presets — dedups across batchers, render
    ///     targets, and textures.
    /// </summary>
    SamplerHandle GetSampler(in SamplerDesc desc);

    // ---- Shaders & pipelines ----

    ShaderHandle CreateShader(ShaderSource source);
    void DestroyShader(ShaderHandle shader);

    RenderPipelineHandle CreateRenderPipeline(in RenderPipelineDesc desc);
    void DestroyRenderPipeline(RenderPipelineHandle pipeline);

    ComputePipelineHandle CreateComputePipeline(in ComputePipelineDesc desc);
    void DestroyComputePipeline(ComputePipelineHandle pipeline);

    // ---- Bind groups ----

    BindGroupLayoutHandle CreateBindGroupLayout(in BindGroupLayoutDesc desc);
    void DestroyBindGroupLayout(BindGroupLayoutHandle layout);

    /// <summary>
    ///     Cached layout accessor. Returns a shared layout for the given entry list — repeated
    ///     calls with the same entries return the same handle (DebugName is ignored). The
    ///     device owns the lifetime: cached layouts are destroyed when the device is disposed.
    ///     Prefer this over <see cref="CreateBindGroupLayout"/> for the well-known shapes
    ///     (one or two CombinedImageSampler at fragment) shared across batchers and effects.
    /// </summary>
    BindGroupLayoutHandle GetBindGroupLayout(in BindGroupLayoutDesc desc);

    BindGroupHandle CreateBindGroup(in BindGroupDesc desc);
    void DestroyBindGroup(BindGroupHandle group);

    // ---- Render targets (convenience around texture + view) ----

    RenderTargetHandle CreateRenderTarget(in RenderTargetDesc desc);
    void DestroyRenderTarget(RenderTargetHandle rt);
    TextureViewHandle GetRenderTargetColorView(RenderTargetHandle rt);
    TextureViewHandle GetRenderTargetDepthView(RenderTargetHandle rt);

    // ---- Queries ----

    QueryPoolHandle CreateQueryPool(in QueryPoolDesc desc);
    void DestroyQueryPool(QueryPoolHandle pool);

    /// <summary>Read query results. Blocks until the queries have completed on the GPU.</summary>
    void ReadQueryPool(QueryPoolHandle pool, int firstQuery, Span<ulong> results);

    /// <summary>GPU timestamp tick period in nanoseconds — multiply raw timestamps by this.</summary>
    double TimestampPeriodNs { get; }

    // ---- Frame lifecycle ----

    /// <summary>
    ///     Acquire the next swapchain image. Must be called before any command encoder this frame.
    ///     Returns false if the swapchain was out of date and the frame must be skipped (no commands,
    ///     submit, or present this frame); the implementation recreates the swapchain so the next call
    ///     succeeds.
    /// </summary>
    bool BeginFrame();

    /// <summary>Open a fresh command encoder. Multiple encoders per frame are allowed.</summary>
    ICommandEncoder BeginCommands(string? debugName = null);

    /// <summary>
    ///     Submit an encoder's commands to the GPU queue. Returns a fence signaled when the work
    ///     finishes — pass <see cref="FenceHandle.Null"/>-tolerant calls if you don't need it.
    /// </summary>
    FenceHandle Submit(ICommandEncoder encoder);

    /// <summary>Present the current swapchain image.</summary>
    void EndFrame();

    /// <summary>Wait on the CPU until <paramref name="fence"/> is signaled (or timeout elapses).</summary>
    bool WaitForFence(FenceHandle fence, TimeSpan timeout);
    bool IsFenceSignaled(FenceHandle fence);
    void DestroyFence(FenceHandle fence);

    /// <summary>Block until all pending GPU work finishes. Use sparingly (resize, shutdown).</summary>
    void WaitIdle();

    // ---- Debug ----

    /// <summary>Set a debug label on any handle. No-op if debug markers are disabled.</summary>
    void SetDebugLabel<T>(T handle, string label) where T : struct;
}
