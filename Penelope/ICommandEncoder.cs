using System.Numerics;

namespace Penelope;

/// <summary>
///     Records GPU commands. Obtained from <see cref="IPenelopeDevice.BeginCommands"/> and
///     submitted via <see cref="IPenelopeDevice.Submit"/>. A single encoder can record multiple
///     render passes, multiple compute passes, and transfer commands between them. Once submitted
///     the encoder is consumed — do not reuse.
/// </summary>
public interface ICommandEncoder : IDisposable
{
    // ---- Passes ----

    IRenderPassEncoder BeginRenderPass(in RenderPassDesc desc);
    IComputePassEncoder BeginComputePass(string? debugName = null);

    /// <summary>
    ///     Close recording on this encoder. Called by <see cref="IPenelopeDevice.Submit"/> before
    ///     the commands are handed to the GPU queue; backends use it to flush deferred work and emit
    ///     end-of-frame transitions (e.g. Vulkan moves swapchain images to <c>PresentSrcKHR</c>).
    ///     Idempotent — a second call after submission is a no-op. After Finish the encoder is
    ///     consumed; do not record further commands.
    /// </summary>
    void Finish();

    // ---- Transfer commands (outside passes) ----

    void CopyBufferToBuffer(
        BufferHandle src, int srcOffset,
        BufferHandle dst, int dstOffset,
        int sizeBytes);

    void CopyBufferToTexture(
        BufferHandle src, int srcOffset, int bytesPerRow, int rowsPerImage,
        TextureHandle dst, int mipLevel, int arrayLayer,
        int x, int y, int z,
        int width, int height, int depth);

    void CopyTextureToBuffer(
        TextureHandle src, int mipLevel, int arrayLayer,
        int x, int y, int z,
        int width, int height, int depth,
        BufferHandle dst, int dstOffset, int bytesPerRow, int rowsPerImage);

    void CopyTextureToTexture(
        TextureHandle src, int srcMipLevel, int srcArrayLayer, int sx, int sy, int sz,
        TextureHandle dst, int dstMipLevel, int dstArrayLayer, int dx, int dy, int dz,
        int width, int height, int depth);

    /// <summary>Generate mipmaps for a texture down from mip 0. Uses a backend blit.</summary>
    void GenerateMipmaps(TextureHandle texture);

    /// <summary>
    ///     Transition the image behind <paramref name="view"/> so it can be sampled in a
    ///     subsequent render pass's fragment shader. Required between passes when one pass
    ///     writes an RT attachment the next pass samples via a bind group. On backends that
    ///     handle layouts implicitly this is a no-op; on Vulkan it emits a barrier from
    ///     <c>ColorAttachmentOptimal</c> to <c>ShaderReadOnlyOptimal</c>.
    /// </summary>
    void MakeTextureSampleable(TextureViewHandle view);

    // ---- Queries (outside passes) ----

    void WriteTimestamp(QueryPoolHandle pool, int queryIndex);
    void ResetQueryPool(QueryPoolHandle pool, int firstQuery, int queryCount);
    void ResolveQueryData(
        QueryPoolHandle pool, int firstQuery, int queryCount,
        BufferHandle dst, int dstOffset);

    // ---- Debug ----

    void PushDebugGroup(string name);
    void PopDebugGroup();
    void InsertDebugMarker(string name);
}

/// <summary>Render pass encoder — active inside a <see cref="ICommandEncoder.BeginRenderPass"/> block.</summary>
public interface IRenderPassEncoder : IDisposable
{
    void End();

    // ---- Viewport / scissor ----

    void SetViewport(float x, float y, float width, float height, float minDepth = 0f, float maxDepth = 1f);
    void SetScissor(int x, int y, int width, int height);
    void SetBlendConstant(float r, float g, float b, float a);
    void SetStencilReference(uint reference);

    // ---- Bindings ----

    void SetPipeline(RenderPipelineHandle pipeline);
    void SetVertexBuffer(int slot, BufferHandle buffer, int offsetBytes = 0);
    void SetIndexBuffer(BufferHandle buffer, IndexType type, int offsetBytes = 0);

    /// <summary>
    ///     Bind a bind group to <paramref name="index"/>. <paramref name="dynamicOffsets"/> supplies
    ///     per-dynamic-buffer byte offsets in layout order (empty if none).
    /// </summary>
    void SetBindGroup(int index, BindGroupHandle group, ReadOnlySpan<int> dynamicOffsets = default);

    /// <summary>Write push constants. Stage must match the pipeline's declared range.</summary>
    void SetPushConstants(ShaderStage stages, int offsetBytes, ReadOnlySpan<byte> data);

    /// <summary>Convenience: single Matrix4x4 push constant at offset 0, all stages.</summary>
    void SetPushConstantMatrix4(ShaderStage stages, int offsetBytes, in Matrix4x4 value);

    // ---- Draws ----

    void Draw(int vertexCount, int instanceCount = 1, int firstVertex = 0, int firstInstance = 0);
    void DrawIndexed(int indexCount, int instanceCount = 1, int firstIndex = 0, int vertexOffset = 0, int firstInstance = 0);
    void DrawIndirect(BufferHandle indirectBuffer, int offsetBytes);
    void DrawIndexedIndirect(BufferHandle indirectBuffer, int offsetBytes);
    void MultiDrawIndirect(BufferHandle indirectBuffer, int offsetBytes, int drawCount, int strideBytes);
    void MultiDrawIndexedIndirect(BufferHandle indirectBuffer, int offsetBytes, int drawCount, int strideBytes);

    // ---- Occlusion queries ----

    void BeginOcclusionQuery(int queryIndex);
    void EndOcclusionQuery();

    // ---- Debug ----

    void PushDebugGroup(string name);
    void PopDebugGroup();
    void InsertDebugMarker(string name);
}

/// <summary>Compute pass encoder — active inside a <see cref="ICommandEncoder.BeginComputePass"/> block.</summary>
public interface IComputePassEncoder : IDisposable
{
    void End();

    void SetPipeline(ComputePipelineHandle pipeline);
    void SetBindGroup(int index, BindGroupHandle group, ReadOnlySpan<int> dynamicOffsets = default);
    void SetPushConstants(ShaderStage stages, int offsetBytes, ReadOnlySpan<byte> data);

    void Dispatch(int groupsX, int groupsY = 1, int groupsZ = 1);
    void DispatchIndirect(BufferHandle indirectBuffer, int offsetBytes);

    void PushDebugGroup(string name);
    void PopDebugGroup();
    void InsertDebugMarker(string name);
}
