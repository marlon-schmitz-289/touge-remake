using System.Numerics;
using System.Runtime.Versioning;
using SharpMetal.Metal;

namespace Penelope.Backends.Metal;

/// <summary>
///     Metal command encoder. One MTLCommandBuffer per frame is shared across all encoders;
///     each Penelope render pass becomes an MTLRenderCommandEncoder, each compute pass a
///     MTLComputeCommandEncoder. Transfer commands outside passes use MTLBlitCommandEncoder.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed unsafe class MetalCommandEncoder : ICommandEncoder
{
    private readonly MetalDevice _dev;
    private readonly MTLCommandBuffer _cb;
    private readonly string? _debugName;
    private bool _disposed;

    /// <summary>Currently-open blit encoder (lazy — created on first transfer command).</summary>
    private MTLBlitCommandEncoder _blit;
    private bool _hasBlit;

    public MetalCommandEncoder(MetalDevice dev, MTLCommandBuffer cb, string? debugName)
    {
        _dev = dev;
        _cb = cb;
        _debugName = debugName;
    }

    public void Finish()
    {
        EndBlitIfActive();
    }

    private MTLBlitCommandEncoder GetBlit()
    {
        if (!_hasBlit) { _blit = _cb.BlitCommandEncoder(); _hasBlit = true; }
        return _blit;
    }

    private void EndBlitIfActive()
    {
        if (_hasBlit) { _blit.EndEncoding(); _hasBlit = false; _blit = default; }
    }

    public IRenderPassEncoder BeginRenderPass(in RenderPassDesc desc)
    {
        EndBlitIfActive();
        using var rpd = new MTLRenderPassDescriptor();
        int width = 0, height = 0;
        for (var i = 0; i < desc.ColorAttachments.Length; i++)
        {
            var a = desc.ColorAttachments[i];
            var view = _dev.GetTextureView(a.View);
            var tex = _dev.GetTexture(view.TextureId);
            var attach = rpd.ColorAttachments.Object((ulong)i);
            attach.Texture = _dev.GetViewTexture(view);
            attach.LoadAction = MetalConvert.ToMetal(a.Load);
            attach.StoreAction = MetalConvert.ToMetal(a.Store);
            attach.ClearColor = new MTLClearColor
            {
                red = a.ClearValue.R, green = a.ClearValue.G,
                blue = a.ClearValue.B, alpha = a.ClearValue.A,
            };
            attach.Level = (ulong)view.BaseMip;
            attach.Slice = (ulong)view.BaseLayer;
            if (!a.ResolveTarget.IsNull)
            {
                var rv = _dev.GetTextureView(a.ResolveTarget);
                attach.ResolveTexture = _dev.GetViewTexture(rv);
                attach.StoreAction = a.Store == StoreOp.Store ? MTLStoreAction.StoreAndMultisampleResolve : MTLStoreAction.MultisampleResolve;
            }
            if (i == 0)
            {
                width = Math.Max(1, tex.Width >> view.BaseMip);
                height = Math.Max(1, tex.Height >> view.BaseMip);
            }
        }
        if (desc.DepthStencilAttachment.HasValue)
        {
            var ds = desc.DepthStencilAttachment.Value;
            var view = _dev.GetTextureView(ds.View);
            var tex = _dev.GetTexture(view.TextureId);
            var dattach = rpd.DepthAttachment;
            dattach.Texture = _dev.GetViewTexture(view);
            dattach.LoadAction = MetalConvert.ToMetal(ds.DepthLoad);
            dattach.StoreAction = MetalConvert.ToMetal(ds.DepthStore);
            dattach.ClearDepth = ds.DepthClear;
            dattach.Level = (ulong)view.BaseMip;
            dattach.Slice = (ulong)view.BaseLayer;
            if (!ds.ResolveTarget.IsNull)
            {
                dattach.ResolveTexture = _dev.GetViewTexture(_dev.GetTextureView(ds.ResolveTarget));
                dattach.StoreAction = ds.DepthStore == StoreOp.Store ? MTLStoreAction.StoreAndMultisampleResolve : MTLStoreAction.MultisampleResolve;
                dattach.DepthResolveFilter = MTLMultisampleDepthResolveFilter.Sample0;
            }
            if (tex.Format is TextureFormat.Depth24PlusStencil8 or TextureFormat.Depth32FloatStencil8)
            {
                var sattach = rpd.StencilAttachment;
                sattach.Texture = _dev.GetViewTexture(view);
                sattach.LoadAction = MetalConvert.ToMetal(ds.StencilLoad);
                sattach.StoreAction = MetalConvert.ToMetal(ds.StencilStore);
                sattach.ClearStencil = ds.StencilClear;
            }
            if (width == 0)
            {
                width = Math.Max(1, tex.Width >> view.BaseMip);
                height = Math.Max(1, tex.Height >> view.BaseMip);
            }
        }

        var encoder = _cb.RenderCommandEncoder(rpd);
        return new MetalRenderPassEncoder(_dev, encoder, width, height);
    }

    public IComputePassEncoder BeginComputePass(string? debugName = null)
    {
        EndBlitIfActive();
        return new MetalComputePassEncoder(_dev, _cb.ComputeCommandEncoder());
    }

    public void CopyBufferToBuffer(BufferHandle src, int srcOffset, BufferHandle dst, int dstOffset, int sizeBytes)
    {
        var s = _dev.GetBuffer(src);
        var d = _dev.GetBuffer(dst);
        GetBlit().CopyFromBuffer(s.Buffer, (ulong)srcOffset, d.Buffer, (ulong)dstOffset, (ulong)sizeBytes);
    }

    public void CopyBufferToTexture(
        BufferHandle src, int srcOffset, int bytesPerRow, int rowsPerImage,
        TextureHandle dst, int mipLevel, int arrayLayer,
        int x, int y, int z, int width, int height, int depth)
    {
        var s = _dev.GetBuffer(src);
        var t = _dev.GetTexture(dst.Id);
        GetBlit().CopyFromBuffer(
            s.Buffer, (ulong)srcOffset,
            (ulong)bytesPerRow, (ulong)(bytesPerRow * rowsPerImage),
            new MTLSize { width = (ulong)width, height = (ulong)height, depth = (ulong)depth },
            t.Texture, (ulong)arrayLayer, (ulong)mipLevel,
            new MTLOrigin { x = (ulong)x, y = (ulong)y, z = (ulong)z });
    }

    public void CopyTextureToBuffer(
        TextureHandle src, int mipLevel, int arrayLayer,
        int x, int y, int z, int width, int height, int depth,
        BufferHandle dst, int dstOffset, int bytesPerRow, int rowsPerImage)
    {
        var s = _dev.GetTexture(src.Id);
        var d = _dev.GetBuffer(dst);
        GetBlit().CopyFromTexture(
            s.Texture, (ulong)arrayLayer, (ulong)mipLevel,
            new MTLOrigin { x = (ulong)x, y = (ulong)y, z = (ulong)z },
            new MTLSize { width = (ulong)width, height = (ulong)height, depth = (ulong)depth },
            d.Buffer, (ulong)dstOffset, (ulong)bytesPerRow, (ulong)(bytesPerRow * rowsPerImage));
    }

    public void CopyTextureToTexture(
        TextureHandle src, int srcMipLevel, int srcArrayLayer, int sx, int sy, int sz,
        TextureHandle dst, int dstMipLevel, int dstArrayLayer, int dx, int dy, int dz,
        int width, int height, int depth)
    {
        var s = _dev.GetTexture(src.Id);
        var d = _dev.GetTexture(dst.Id);
        GetBlit().CopyFromTexture(
            s.Texture, (ulong)srcArrayLayer, (ulong)srcMipLevel,
            new MTLOrigin { x = (ulong)sx, y = (ulong)sy, z = (ulong)sz },
            new MTLSize { width = (ulong)width, height = (ulong)height, depth = (ulong)depth },
            d.Texture, (ulong)dstArrayLayer, (ulong)dstMipLevel,
            new MTLOrigin { x = (ulong)dx, y = (ulong)dy, z = (ulong)dz });
    }

    public void GenerateMipmaps(TextureHandle texture)
    {
        var t = _dev.GetTexture(texture.Id);
        GetBlit().GenerateMipmaps(t.Texture);
    }

    public void MakeTextureSampleable(TextureViewHandle view)
    {
        // Metal handles read-after-write hazards automatically when textures are tracked
        // (the default). No barrier needed here.
    }

    public void WriteTimestamp(QueryPoolHandle pool, int queryIndex) { /* TODO: MTLCounterSampleBuffer */ }
    public void ResetQueryPool(QueryPoolHandle pool, int firstQuery, int queryCount) { }
    public void ResolveQueryData(QueryPoolHandle pool, int firstQuery, int queryCount, BufferHandle dst, int dstOffset) { }

    public void PushDebugGroup(string name) => _cb.PushDebugGroup(name);
    public void PopDebugGroup() => _cb.PopDebugGroup();
    public void InsertDebugMarker(string name) { /* not exposed on MTLCommandBuffer */ }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Finish();
    }
}

[SupportedOSPlatform("macos")]
internal sealed unsafe class MetalRenderPassEncoder : IRenderPassEncoder
{
    private readonly MetalDevice _dev;
    private readonly MTLRenderCommandEncoder _enc;
    private readonly int _width, _height;
    private MetalRenderPipeline? _pipeline;
    private MTLBuffer _indexBuffer;
    private bool _hasIndexBuffer;
    private int _indexOffsetBytes;
    private int _indexSize = 4;
    private MTLIndexType _indexType = MTLIndexType.UInt32;
    private bool _ended;

    public MetalRenderPassEncoder(MetalDevice dev, MTLRenderCommandEncoder enc, int w, int h)
    {
        _dev = dev;
        _enc = enc;
        _width = w;
        _height = h;
    }

    public void End()
    {
        if (_ended) return;
        _ended = true;
        _enc.EndEncoding();
    }
    public void Dispose() => End();

    public void SetViewport(float x, float y, float width, float height, float minDepth = 0f, float maxDepth = 1f)
    {
        // Metal viewport y is top-down (matches Vulkan and the engine's top-left convention).
        // Negative width/height (Vulkan-style flip) — abs them; Metal has no negative-viewport path.
        if (height < 0) { y += height; height = -height; }
        if (width < 0)  { x += width;  width = -width; }
        _enc.SetViewport(new MTLViewport
        {
            originX = x, originY = y,
            width = width, height = height,
            znear = minDepth, zfar = maxDepth,
        });
    }

    public void SetScissor(int x, int y, int width, int height)
    {
        if (height < 0) { y += height; height = -height; }
        if (width < 0)  { x += width;  width = -width; }
        // Clamp to attachment so out-of-range scissor doesn't trip Metal validation.
        x = Math.Clamp(x, 0, _width);
        y = Math.Clamp(y, 0, _height);
        width = Math.Min(width, _width - x);
        height = Math.Min(height, _height - y);
        _enc.SetScissorRect(new MTLScissorRect
        {
            x = (ulong)x, y = (ulong)y,
            width = (ulong)Math.Max(0, width), height = (ulong)Math.Max(0, height),
        });
    }

    public void SetBlendConstant(float r, float g, float b, float a)
        => _enc.SetBlendColor(r, g, b, a);

    public void SetStencilReference(uint reference)
        => _enc.SetStencilReferenceValue(reference);

    public void SetPipeline(RenderPipelineHandle pipeline)
    {
        _pipeline = _dev.GetRenderPipeline(pipeline);
        _enc.SetRenderPipelineState(_pipeline.Pipeline);
        if (_pipeline.HasDepthStencilState) _enc.SetDepthStencilState(_pipeline.DepthStencilState);
        _enc.SetCullMode(_pipeline.CullMode);
        _enc.SetFrontFacingWinding(_pipeline.Winding);
        _enc.SetTriangleFillMode(_pipeline.FillMode);
        if (_pipeline.DepthBiasConstant != 0 || _pipeline.DepthBiasSlope != 0)
            _enc.SetDepthBias(_pipeline.DepthBiasConstant, _pipeline.DepthBiasSlope, _pipeline.DepthBiasClamp);
    }

    public void SetVertexBuffer(int slot, BufferHandle buffer, int offsetBytes = 0)
    {
        var buf = _dev.GetBuffer(buffer);
        _enc.SetVertexBuffer(buf.Buffer, (ulong)offsetBytes, MetalDevice.MtlVertexBufferIndex(slot));
    }

    public void SetIndexBuffer(BufferHandle buffer, IndexType type, int offsetBytes = 0)
    {
        // Metal binds the index buffer per-draw-call (DrawIndexedPrimitives takes it). Cache.
        var buf = _dev.GetBuffer(buffer);
        _indexBuffer = buf.Buffer;
        _hasIndexBuffer = true;
        _indexOffsetBytes = offsetBytes;
        _indexType = MetalConvert.ToMetal(type);
        _indexSize = type == IndexType.UInt16 ? 2 : 4;
    }

    public void SetBindGroup(int index, BindGroupHandle group, ReadOnlySpan<int> dynamicOffsets = default)
    {
        var bg = _dev.GetBindGroup(group);
        var layout = _dev.GetBindGroupLayout(bg.LayoutId);
        var dynIdx = 0;
        foreach (var entry in bg.Entries)
        {
            var le = FindLayoutEntry(layout, entry.Binding);
            // Penelope's (set, binding) flattens to a single Metal slot. SPIRV-Cross's MSL
            // emitter assigns matching numeric bindings, so we use the binding number directly.
            var stages = le.Visibility;
            switch (le.Type)
            {
                case BindingType.UniformBuffer:
                case BindingType.StorageBuffer:
                case BindingType.ReadOnlyStorageBuffer:
                {
                    var buf = _dev.GetBuffer(entry.Buffer);
                    var off = entry.BufferOffset + (le.HasDynamicOffset && dynIdx < dynamicOffsets.Length
                        ? dynamicOffsets[dynIdx++] : 0);
                    if ((stages & ShaderStage.Vertex) != 0)
                        _enc.SetVertexBuffer(buf.Buffer, (ulong)off, (ulong)entry.Binding);
                    if ((stages & ShaderStage.Fragment) != 0)
                        _enc.SetFragmentBuffer(buf.Buffer, (ulong)off, (ulong)entry.Binding);
                    break;
                }
                case BindingType.SampledTexture:
                case BindingType.StorageTexture:
                case BindingType.ReadOnlyStorageTexture:
                {
                    var view = _dev.GetTextureView(entry.TextureView);
                    var tex = _dev.GetViewTexture(view);
                    if ((stages & ShaderStage.Vertex) != 0)
                        _enc.SetVertexTexture(tex, (ulong)entry.Binding);
                    if ((stages & ShaderStage.Fragment) != 0)
                        _enc.SetFragmentTexture(tex, (ulong)entry.Binding);
                    break;
                }
                case BindingType.Sampler:
                case BindingType.ComparisonSampler:
                {
                    var samp = _dev.GetSampler(entry.Sampler);
                    if ((stages & ShaderStage.Vertex) != 0)
                        _enc.SetVertexSamplerState(samp.Sampler, (ulong)entry.Binding);
                    if ((stages & ShaderStage.Fragment) != 0)
                        _enc.SetFragmentSamplerState(samp.Sampler, (ulong)entry.Binding);
                    break;
                }
                case BindingType.CombinedImageSampler:
                {
                    var view = _dev.GetTextureView(entry.TextureView);
                    var tex = _dev.GetViewTexture(view);
                    var samp = _dev.GetSampler(entry.Sampler);
                    if ((stages & ShaderStage.Vertex) != 0)
                    {
                        _enc.SetVertexTexture(tex, (ulong)entry.Binding);
                        _enc.SetVertexSamplerState(samp.Sampler, (ulong)entry.Binding);
                    }
                    if ((stages & ShaderStage.Fragment) != 0)
                    {
                        _enc.SetFragmentTexture(tex, (ulong)entry.Binding);
                        _enc.SetFragmentSamplerState(samp.Sampler, (ulong)entry.Binding);
                    }
                    break;
                }
            }
        }
    }

    private static BindGroupLayoutEntry FindLayoutEntry(MetalBindGroupLayout layout, int binding)
    {
        foreach (var e in layout.Entries) if (e.Binding == binding) return e;
        throw new ArgumentException($"Binding {binding} not found in layout.");
    }

    public void SetPushConstants(ShaderStage stages, int offsetBytes, ReadOnlySpan<byte> data)
    {
        // Metal's SetVertexBytes/SetFragmentBytes uploads up to 4 KB inline (no MTLBuffer).
        // SPIRV-Cross's MSL emitter places the push-constant block at MetalDevice.PushConstantBufferIndex.
        fixed (byte* p = data)
        {
            if ((stages & ShaderStage.Vertex) != 0)
                _enc.SetVertexBytes((nint)(p + offsetBytes), (ulong)data.Length, MetalDevice.PushConstantBufferIndex);
            if ((stages & ShaderStage.Fragment) != 0)
                _enc.SetFragmentBytes((nint)(p + offsetBytes), (ulong)data.Length, MetalDevice.PushConstantBufferIndex);
        }
    }

    public void SetPushConstantMatrix4(ShaderStage stages, int offsetBytes, in Matrix4x4 value)
    {
        var m = value;
        var span = new ReadOnlySpan<byte>(&m, sizeof(Matrix4x4));
        SetPushConstants(stages, offsetBytes, span);
    }

    public void Draw(int vertexCount, int instanceCount = 1, int firstVertex = 0, int firstInstance = 0)
    {
        if (_pipeline == null) return;
        if (firstInstance == 0)
            _enc.DrawPrimitives(_pipeline.Topology, (ulong)firstVertex, (ulong)vertexCount, (ulong)instanceCount);
        else
            _enc.DrawPrimitives(_pipeline.Topology, (ulong)firstVertex, (ulong)vertexCount, (ulong)instanceCount, (ulong)firstInstance);
    }

    public void DrawIndexed(int indexCount, int instanceCount = 1, int firstIndex = 0, int vertexOffset = 0, int firstInstance = 0)
    {
        if (_pipeline == null || !_hasIndexBuffer) return;
        var bufferOffset = (ulong)(_indexOffsetBytes + firstIndex * _indexSize);
        if (vertexOffset == 0 && firstInstance == 0)
            _enc.DrawIndexedPrimitives(_pipeline.Topology, (ulong)indexCount, _indexType,
                _indexBuffer, bufferOffset, (ulong)instanceCount);
        else
            _enc.DrawIndexedPrimitives(_pipeline.Topology, (ulong)indexCount, _indexType,
                _indexBuffer, bufferOffset, (ulong)instanceCount, vertexOffset, (ulong)firstInstance);
    }

    public void DrawIndirect(BufferHandle indirectBuffer, int offsetBytes)
    {
        if (_pipeline == null) return;
        var buf = _dev.GetBuffer(indirectBuffer);
        _enc.DrawPrimitives(_pipeline.Topology, buf.Buffer, (ulong)offsetBytes);
    }

    public void DrawIndexedIndirect(BufferHandle indirectBuffer, int offsetBytes)
    {
        if (_pipeline == null || !_hasIndexBuffer) return;
        var buf = _dev.GetBuffer(indirectBuffer);
        _enc.DrawIndexedPrimitives(_pipeline.Topology, _indexType,
            _indexBuffer, (ulong)_indexOffsetBytes, buf.Buffer, (ulong)offsetBytes);
    }

    public void MultiDrawIndirect(BufferHandle indirectBuffer, int offsetBytes, int drawCount, int strideBytes)
    {
        // Metal has no multi-draw-indirect; emulate via N indirect draws.
        for (var i = 0; i < drawCount; i++)
            DrawIndirect(indirectBuffer, offsetBytes + i * strideBytes);
    }

    public void MultiDrawIndexedIndirect(BufferHandle indirectBuffer, int offsetBytes, int drawCount, int strideBytes)
    {
        for (var i = 0; i < drawCount; i++)
            DrawIndexedIndirect(indirectBuffer, offsetBytes + i * strideBytes);
    }

    public void BeginOcclusionQuery(int queryIndex) { /* TODO: visibilityResultBuffer on render-pass desc */ }
    public void EndOcclusionQuery() { }

    public void PushDebugGroup(string name) => _enc.PushDebugGroup(name);
    public void PopDebugGroup() => _enc.PopDebugGroup();
    public void InsertDebugMarker(string name) => _enc.InsertDebugSignpost(name);
}

[SupportedOSPlatform("macos")]
internal sealed unsafe class MetalComputePassEncoder : IComputePassEncoder
{
    private readonly MetalDevice _dev;
    private readonly MTLComputeCommandEncoder _enc;
    private MetalComputePipeline? _pipeline;
    private bool _ended;

    public MetalComputePassEncoder(MetalDevice dev, MTLComputeCommandEncoder enc)
    {
        _dev = dev;
        _enc = enc;
    }

    public void End()
    {
        if (_ended) return;
        _ended = true;
        _enc.EndEncoding();
    }
    public void Dispose() => End();

    public void SetPipeline(ComputePipelineHandle pipeline)
    {
        _pipeline = _dev.GetComputePipeline(pipeline);
        _enc.SetComputePipelineState(_pipeline.Pipeline);
    }

    public void SetBindGroup(int index, BindGroupHandle group, ReadOnlySpan<int> dynamicOffsets = default)
    {
        var bg = _dev.GetBindGroup(group);
        var layout = _dev.GetBindGroupLayout(bg.LayoutId);
        var dynIdx = 0;
        foreach (var entry in bg.Entries)
        {
            var le = FindLayoutEntry(layout, entry.Binding);
            switch (le.Type)
            {
                case BindingType.UniformBuffer:
                case BindingType.StorageBuffer:
                case BindingType.ReadOnlyStorageBuffer:
                {
                    var buf = _dev.GetBuffer(entry.Buffer);
                    var off = entry.BufferOffset + (le.HasDynamicOffset && dynIdx < dynamicOffsets.Length
                        ? dynamicOffsets[dynIdx++] : 0);
                    _enc.SetBuffer(buf.Buffer, (ulong)off, (ulong)entry.Binding);
                    break;
                }
                case BindingType.SampledTexture:
                case BindingType.StorageTexture:
                case BindingType.ReadOnlyStorageTexture:
                {
                    var view = _dev.GetTextureView(entry.TextureView);
                    _enc.SetTexture(_dev.GetViewTexture(view), (ulong)entry.Binding);
                    break;
                }
                case BindingType.Sampler:
                case BindingType.ComparisonSampler:
                {
                    var samp = _dev.GetSampler(entry.Sampler);
                    _enc.SetSamplerState(samp.Sampler, (ulong)entry.Binding);
                    break;
                }
                case BindingType.CombinedImageSampler:
                {
                    var view = _dev.GetTextureView(entry.TextureView);
                    var samp = _dev.GetSampler(entry.Sampler);
                    _enc.SetTexture(_dev.GetViewTexture(view), (ulong)entry.Binding);
                    _enc.SetSamplerState(samp.Sampler, (ulong)entry.Binding);
                    break;
                }
            }
        }
    }

    private static BindGroupLayoutEntry FindLayoutEntry(MetalBindGroupLayout layout, int binding)
    {
        foreach (var e in layout.Entries) if (e.Binding == binding) return e;
        throw new ArgumentException($"Binding {binding} not found in layout.");
    }

    public void SetPushConstants(ShaderStage stages, int offsetBytes, ReadOnlySpan<byte> data)
    {
        fixed (byte* p = data)
            _enc.SetBytes((nint)(p + offsetBytes), (ulong)data.Length, MetalDevice.PushConstantBufferIndex);
    }

    public void Dispatch(int groupsX, int groupsY = 1, int groupsZ = 1)
    {
        if (_pipeline == null) return;
        var groups = new MTLSize { width = (ulong)groupsX, height = (ulong)groupsY, depth = (ulong)groupsZ };
        _enc.DispatchThreadgroups(groups, _pipeline.ThreadsPerGroup);
    }

    public void DispatchIndirect(BufferHandle indirectBuffer, int offsetBytes)
    {
        if (_pipeline == null) return;
        var b = _dev.GetBuffer(indirectBuffer);
        _enc.DispatchThreadgroups(b.Buffer, (ulong)offsetBytes, _pipeline.ThreadsPerGroup);
    }

    public void PushDebugGroup(string name) => _enc.PushDebugGroup(name);
    public void PopDebugGroup() => _enc.PopDebugGroup();
    public void InsertDebugMarker(string name) => _enc.InsertDebugSignpost(name);
}
