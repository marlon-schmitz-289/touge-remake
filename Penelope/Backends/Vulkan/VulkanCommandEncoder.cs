using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace Penelope.Backends.Vulkan;

internal sealed unsafe class VulkanCommandEncoder : ICommandEncoder
{
    private readonly VulkanDevice _dev;
    public readonly CommandBuffer CommandBuffer;
    private readonly string? _debugName;
    private bool _ended;
    public bool Submitted { get; private set; }
    public bool IsInvalid { get; private set; }

    /// <summary>Tracks swapchain views used this submission — transition them to PresentSrc at end.</summary>
    private readonly List<ulong> _swapchainViewsUsed = new();

    /// <summary>
    ///     True if this encoder rendered to a swapchain image (valid after Finish()). The submit that
    ///     touched the swapchain is the one that signals that image's present-wait semaphore.
    /// </summary>
    internal bool TouchedSwapchain => _swapchainViewsUsed.Count > 0;

    /// <summary>Reused across BeginRenderPass calls on this encoder; grows to the largest attachment count seen.</summary>
    private RenderingAttachmentInfo[] _colorScratch = new RenderingAttachmentInfo[4];

    public VulkanCommandEncoder(VulkanDevice dev, CommandBuffer cb, string? debugName)
    {
        _dev = dev;
        CommandBuffer = cb;
        _debugName = debugName;
    }

    internal void Invalidate()
    {
        IsInvalid = true;
        Submitted = true;
    }

    internal void MarkSwapchainView(ulong viewId) => _swapchainViewsUsed.Add(viewId);

    public void Finish()
    {
        if (_ended) return;

        // Transition any swapchain images we rendered to from ColorAttachmentOptimal to PresentSrcKHR.
        foreach (var vid in _swapchainViewsUsed)
        {
            if (!_dev.TryGetImageView(vid, out var view)) continue;
            if (!_dev.TryGetImage(view.ImageId, out var img)) continue;
            if (!img.IsSwapchainImage) continue;
            _dev.TransitionImageLayout(
                CommandBuffer, img.Image, img.Format,
                img.CurrentLayout == ImageLayout.Undefined ? ImageLayout.ColorAttachmentOptimal : img.CurrentLayout,
                ImageLayout.PresentSrcKhr,
                0, 1, 0, 1);
            img.CurrentLayout = ImageLayout.PresentSrcKhr;
        }

        _dev.Vk.EndCommandBuffer(CommandBuffer).ThrowIfError();
        _ended = true;
    }

    public IRenderPassEncoder BeginRenderPass(in RenderPassDesc desc)
    {
        // Transition color attachments to ColorAttachmentOptimal
        for (var i = 0; i < desc.ColorAttachments.Length; i++)
        {
            var att = desc.ColorAttachments[i];
            var view = _dev.GetImageView(att.View);
            var img = _dev.GetImage(view.ImageId);
            var newLayout = ImageLayout.ColorAttachmentOptimal;
            // When the pass clears or discards, prior contents are dead, so transition FROM Undefined
            // rather than the tracked layout. This avoids depending on the previous layout — e.g. the
            // swapchain image's PresentSrc->ColorAttachment each frame, which otherwise hits the
            // AllCommands->AllCommands full-flush catch-all — and lets RADV use DCC fast-clear. The
            // ImageAvailable semaphore (waited at ColorAttachmentOutput) already orders the write after
            // the prior present, so dropping the layout dependency is safe.
            var oldLayout = att.Load is LoadOp.Clear or LoadOp.DontCare
                ? ImageLayout.Undefined
                : img.CurrentLayout;
            if (oldLayout != newLayout)
            {
                _dev.TransitionImageLayout(
                    CommandBuffer, img.Image, img.Format,
                    oldLayout, newLayout,
                    view.BaseMip, view.MipCount, view.BaseLayer, view.LayerCount);
            }
            img.CurrentLayout = newLayout;
            if (img.IsSwapchainImage) MarkSwapchainView(att.View.Id);
        }

        if (desc.DepthStencilAttachment.HasValue)
        {
            var att = desc.DepthStencilAttachment.Value;
            var view = _dev.GetImageView(att.View);
            var img = _dev.GetImage(view.ImageId);
            var newLayout = ImageLayout.DepthStencilAttachmentOptimal;
            if (img.CurrentLayout != newLayout)
            {
                _dev.TransitionImageLayout(
                    CommandBuffer, img.Image, img.Format,
                    img.CurrentLayout, newLayout,
                    view.BaseMip, view.MipCount, view.BaseLayer, view.LayerCount);
                img.CurrentLayout = newLayout;
            }
        }

        // Build VkRenderingInfo (dynamic rendering). Reuse a per-encoder scratch array instead of
        // allocating one per render pass; grow it only if a pass ever needs more attachments.
        var colorCount = desc.ColorAttachments.Length;
        if (_colorScratch.Length < colorCount)
            _colorScratch = new RenderingAttachmentInfo[colorCount];
        var colorInfos = _colorScratch;
        for (var i = 0; i < desc.ColorAttachments.Length; i++)
        {
            var a = desc.ColorAttachments[i];
            var view = _dev.GetImageView(a.View);
            var clearValue = new ClearValue
            {
                Color = new ClearColorValue(a.ClearValue.R, a.ClearValue.G, a.ClearValue.B, a.ClearValue.A),
            };
            colorInfos[i] = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = view.View,
                ImageLayout = ImageLayout.ColorAttachmentOptimal,
                LoadOp = VulkanConvert.ToVk(a.Load),
                StoreOp = VulkanConvert.ToVk(a.Store),
                ClearValue = clearValue,
            };
            if (!a.ResolveTarget.IsNull)
            {
                var resolve = _dev.GetImageView(a.ResolveTarget);
                colorInfos[i].ResolveImageView = resolve.View;
                colorInfos[i].ResolveImageLayout = ImageLayout.ColorAttachmentOptimal;
                colorInfos[i].ResolveMode = ResolveModeFlags.AverageBit;
            }
        }

        var hasDepth = desc.DepthStencilAttachment.HasValue;
        RenderingAttachmentInfo depthInfo = default;
        RenderingAttachmentInfo stencilInfo = default;
        var hasStencil = false;
        if (hasDepth)
        {
            var a = desc.DepthStencilAttachment!.Value;
            var view = _dev.GetImageView(a.View);
            depthInfo = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = view.View,
                ImageLayout = ImageLayout.DepthStencilAttachmentOptimal,
                LoadOp = VulkanConvert.ToVk(a.DepthLoad),
                StoreOp = VulkanConvert.ToVk(a.DepthStore),
                ClearValue = new ClearValue
                {
                    DepthStencil = new ClearDepthStencilValue(a.DepthClear, a.StencilClear),
                },
            };
            if ((view.Aspect & ImageAspectFlags.StencilBit) != 0)
            {
                hasStencil = true;
                stencilInfo = new RenderingAttachmentInfo
                {
                    SType = StructureType.RenderingAttachmentInfo,
                    ImageView = view.View,
                    ImageLayout = ImageLayout.DepthStencilAttachmentOptimal,
                    LoadOp = VulkanConvert.ToVk(a.StencilLoad),
                    StoreOp = VulkanConvert.ToVk(a.StencilStore),
                    ClearValue = new ClearValue
                    {
                        DepthStencil = new ClearDepthStencilValue(a.DepthClear, a.StencilClear),
                    },
                };
            }
        }

        // Compute render area from first color attachment's underlying image
        var area = ComputeRenderArea(desc);

        fixed (RenderingAttachmentInfo* pColor = colorInfos)
        {
            var info = new RenderingInfo
            {
                SType = StructureType.RenderingInfo,
                RenderArea = new Rect2D(default, area),
                LayerCount = 1,
                ColorAttachmentCount = (uint)colorCount,
                PColorAttachments = pColor,
                PDepthAttachment = hasDepth ? &depthInfo : null,
                PStencilAttachment = hasStencil ? &stencilInfo : null,
            };
            _dev.Vk.CmdBeginRendering(CommandBuffer, &info);
        }

        // Default viewport + scissor to render area
        var vp = new Viewport(0, 0, area.Width, area.Height, 0f, 1f);
        _dev.Vk.CmdSetViewport(CommandBuffer, 0, 1, &vp);
        var scissor = new Rect2D(default, area);
        _dev.Vk.CmdSetScissor(CommandBuffer, 0, 1, &scissor);

        return new VulkanRenderPassEncoder(_dev, this, CommandBuffer);
    }

    private Extent2D ComputeRenderArea(in RenderPassDesc desc)
    {
        if (desc.ColorAttachments.Length > 0)
        {
            var v = _dev.GetImageView(desc.ColorAttachments[0].View);
            var img = _dev.GetImage(v.ImageId);
            return new Extent2D(img.Extent.Width, img.Extent.Height);
        }
        if (desc.DepthStencilAttachment.HasValue)
        {
            var v = _dev.GetImageView(desc.DepthStencilAttachment.Value.View);
            var img = _dev.GetImage(v.ImageId);
            return new Extent2D(img.Extent.Width, img.Extent.Height);
        }
        return default;
    }

    public IComputePassEncoder BeginComputePass(string? debugName = null)
    {
        return new VulkanComputePassEncoder(_dev, this, CommandBuffer);
    }

    public void CopyBufferToBuffer(BufferHandle src, int srcOffset, BufferHandle dst, int dstOffset, int sizeBytes)
    {
        var s = _dev.GetBuffer(src);
        var d = _dev.GetBuffer(dst);
        var region = new BufferCopy((ulong)srcOffset, (ulong)dstOffset, (ulong)sizeBytes);
        _dev.Vk.CmdCopyBuffer(CommandBuffer, s.Buffer, d.Buffer, 1, &region);
    }

    public void CopyBufferToTexture(
        BufferHandle src, int srcOffset, int bytesPerRow, int rowsPerImage,
        TextureHandle dst, int mipLevel, int arrayLayer,
        int x, int y, int z, int width, int height, int depth)
    {
        var s = _dev.GetBuffer(src);
        var img = _dev.GetImage(dst.Id);

        // Ensure transfer dst layout
        if (img.CurrentLayout != ImageLayout.TransferDstOptimal)
        {
            _dev.TransitionImageLayout(CommandBuffer, img.Image, img.Format,
                img.CurrentLayout, ImageLayout.TransferDstOptimal,
                (uint)mipLevel, 1, (uint)arrayLayer, 1);
            img.CurrentLayout = ImageLayout.TransferDstOptimal;
        }

        var region = new BufferImageCopy
        {
            BufferOffset = (ulong)srcOffset,
            // BufferRowLength is in TEXELS, not bytes. 0 means tightly packed (Vulkan derives it
            // from ImageExtent). For a padded source pitch, convert the byte stride to texels.
            BufferRowLength = bytesPerRow <= 0
                ? 0u
                : (uint)(bytesPerRow / VulkanDevice.BytesPerPixel(VulkanConvert.FromVk(img.Format))),
            BufferImageHeight = 0,
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, (uint)mipLevel, (uint)arrayLayer, 1),
            ImageOffset = new Offset3D(x, y, z),
            ImageExtent = new Extent3D((uint)width, (uint)height, (uint)depth),
        };
        _dev.Vk.CmdCopyBufferToImage(CommandBuffer, s.Buffer, img.Image,
            ImageLayout.TransferDstOptimal, 1, &region);
    }

    public void CopyTextureToBuffer(
        TextureHandle src, int mipLevel, int arrayLayer,
        int x, int y, int z, int width, int height, int depth,
        BufferHandle dst, int dstOffset, int bytesPerRow, int rowsPerImage)
    {
        var img = _dev.GetImage(src.Id);
        var b = _dev.GetBuffer(dst);
        if (img.CurrentLayout != ImageLayout.TransferSrcOptimal)
        {
            _dev.TransitionImageLayout(CommandBuffer, img.Image, img.Format,
                img.CurrentLayout, ImageLayout.TransferSrcOptimal,
                (uint)mipLevel, 1, (uint)arrayLayer, 1);
            img.CurrentLayout = ImageLayout.TransferSrcOptimal;
        }
        var region = new BufferImageCopy
        {
            BufferOffset = (ulong)dstOffset,
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, (uint)mipLevel, (uint)arrayLayer, 1),
            ImageOffset = new Offset3D(x, y, z),
            ImageExtent = new Extent3D((uint)width, (uint)height, (uint)depth),
        };
        _dev.Vk.CmdCopyImageToBuffer(CommandBuffer, img.Image,
            ImageLayout.TransferSrcOptimal, b.Buffer, 1, &region);
    }

    public void CopyTextureToTexture(
        TextureHandle src, int srcMipLevel, int srcArrayLayer, int sx, int sy, int sz,
        TextureHandle dst, int dstMipLevel, int dstArrayLayer, int dx, int dy, int dz,
        int width, int height, int depth)
    {
        var s = _dev.GetImage(src.Id);
        var d = _dev.GetImage(dst.Id);
        var region = new ImageCopy
        {
            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, (uint)srcMipLevel, (uint)srcArrayLayer, 1),
            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, (uint)dstMipLevel, (uint)dstArrayLayer, 1),
            SrcOffset = new Offset3D(sx, sy, sz),
            DstOffset = new Offset3D(dx, dy, dz),
            Extent = new Extent3D((uint)width, (uint)height, (uint)depth),
        };
        _dev.Vk.CmdCopyImage(CommandBuffer,
            s.Image, ImageLayout.TransferSrcOptimal,
            d.Image, ImageLayout.TransferDstOptimal,
            1, &region);
    }

    public void GenerateMipmaps(TextureHandle texture)
    {
        var img = _dev.GetImage(texture.Id);
        int mipW = (int)img.Extent.Width, mipH = (int)img.Extent.Height;

        for (uint i = 1; i < img.MipLevels; i++)
        {
            _dev.TransitionImageLayout(CommandBuffer, img.Image, img.Format,
                ImageLayout.TransferDstOptimal, ImageLayout.TransferSrcOptimal,
                i - 1, 1, 0, img.ArrayLayers);

            var blit = new ImageBlit
            {
                SrcOffsets = new ImageBlit.SrcOffsetsBuffer(),
                DstOffsets = new ImageBlit.DstOffsetsBuffer(),
                SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, i - 1, 0, img.ArrayLayers),
                DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, i, 0, img.ArrayLayers),
            };
            blit.SrcOffsets[1] = new Offset3D(mipW, mipH, 1);
            var nextW = Math.Max(mipW / 2, 1);
            var nextH = Math.Max(mipH / 2, 1);
            blit.DstOffsets[1] = new Offset3D(nextW, nextH, 1);

            _dev.Vk.CmdBlitImage(CommandBuffer,
                img.Image, ImageLayout.TransferSrcOptimal,
                img.Image, ImageLayout.TransferDstOptimal,
                1, &blit, Filter.Linear);

            _dev.TransitionImageLayout(CommandBuffer, img.Image, img.Format,
                ImageLayout.TransferSrcOptimal, ImageLayout.ShaderReadOnlyOptimal,
                i - 1, 1, 0, img.ArrayLayers);

            mipW = nextW;
            mipH = nextH;
        }

        _dev.TransitionImageLayout(CommandBuffer, img.Image, img.Format,
            ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal,
            img.MipLevels - 1, 1, 0, img.ArrayLayers);
        img.CurrentLayout = ImageLayout.ShaderReadOnlyOptimal;
    }

    public void MakeTextureSampleable(TextureViewHandle view)
    {
        var v = _dev.GetImageView(view);
        var img = _dev.GetImage(v.ImageId);
        if (img.CurrentLayout == ImageLayout.ShaderReadOnlyOptimal) return;
        _dev.TransitionImageLayout(CommandBuffer, img.Image, img.Format,
            img.CurrentLayout == ImageLayout.Undefined ? ImageLayout.ColorAttachmentOptimal : img.CurrentLayout,
            ImageLayout.ShaderReadOnlyOptimal,
            0, img.MipLevels, 0, img.ArrayLayers);
        img.CurrentLayout = ImageLayout.ShaderReadOnlyOptimal;
    }

    public void WriteTimestamp(QueryPoolHandle pool, int queryIndex)
    {
        var p = _dev.GetQueryPool(pool);
        _dev.Vk.CmdWriteTimestamp(CommandBuffer, PipelineStageFlags.BottomOfPipeBit, p.Pool, (uint)queryIndex);
    }

    public void ResetQueryPool(QueryPoolHandle pool, int firstQuery, int queryCount)
    {
        var p = _dev.GetQueryPool(pool);
        _dev.Vk.CmdResetQueryPool(CommandBuffer, p.Pool, (uint)firstQuery, (uint)queryCount);
    }

    public void ResolveQueryData(QueryPoolHandle pool, int firstQuery, int queryCount, BufferHandle dst, int dstOffset)
    {
        var p = _dev.GetQueryPool(pool);
        var b = _dev.GetBuffer(dst);
        _dev.Vk.CmdCopyQueryPoolResults(CommandBuffer, p.Pool,
            (uint)firstQuery, (uint)queryCount, b.Buffer, (ulong)dstOffset,
            sizeof(ulong), QueryResultFlags.Result64Bit | QueryResultFlags.ResultWaitBit);
    }

    public void PushDebugGroup(string name) => DebugPush(_dev, CommandBuffer, name);
    public void PopDebugGroup() => DebugPop(_dev, CommandBuffer);
    public void InsertDebugMarker(string name) => DebugMarker(_dev, CommandBuffer, name);

    internal static void DebugPush(VulkanDevice dev, CommandBuffer cb, string name)
    {
        // Requires VK_EXT_debug_utils. Omitted wiring for brevity; safe no-op.
    }

    internal static void DebugPop(VulkanDevice dev, CommandBuffer cb) { }
    internal static void DebugMarker(VulkanDevice dev, CommandBuffer cb, string name) { }

    public void Dispose()
    {
        if (!_ended && !IsInvalid) Finish();
    }
}

internal sealed unsafe class VulkanRenderPassEncoder : IRenderPassEncoder
{
    private readonly VulkanDevice _dev;
    private readonly VulkanCommandEncoder _parent;
    private readonly CommandBuffer _cb;
    private VulkanRenderPipeline? _currentPipeline;
    private bool _ended;

    public VulkanRenderPassEncoder(VulkanDevice dev, VulkanCommandEncoder parent, CommandBuffer cb)
    {
        _dev = dev;
        _parent = parent;
        _cb = cb;
    }

    public void End()
    {
        if (_ended) return;
        _dev.Vk.CmdEndRendering(_cb);
        _ended = true;
    }

    public void Dispose() => End();

    public void SetViewport(float x, float y, float width, float height, float minDepth = 0f, float maxDepth = 1f)
    {
        var vp = new Viewport(x, y, width, height, minDepth, maxDepth);
        _dev.Vk.CmdSetViewport(_cb, 0, 1, &vp);
    }

    public void SetScissor(int x, int y, int width, int height)
    {
        var r = new Rect2D(new Offset2D(x, y), new Extent2D((uint)width, (uint)height));
        _dev.Vk.CmdSetScissor(_cb, 0, 1, &r);
    }

    public void SetBlendConstant(float r, float g, float b, float a)
    {
        var c = stackalloc float[] { r, g, b, a };
        _dev.Vk.CmdSetBlendConstants(_cb, c);
    }

    public void SetStencilReference(uint reference)
    {
        _dev.Vk.CmdSetStencilReference(_cb, StencilFaceFlags.FrontAndBack, reference);
    }

    public void SetPipeline(RenderPipelineHandle pipeline)
    {
        _currentPipeline = _dev.GetRenderPipeline(pipeline);
        _dev.Vk.CmdBindPipeline(_cb, PipelineBindPoint.Graphics, _currentPipeline.Pipeline);
    }

    public void SetVertexBuffer(int slot, BufferHandle buffer, int offsetBytes = 0)
    {
        var buf = _dev.GetBuffer(buffer);
        var v = buf.Buffer;
        var off = (ulong)offsetBytes;
        _dev.Vk.CmdBindVertexBuffers(_cb, (uint)slot, 1, &v, &off);
    }

    public void SetIndexBuffer(BufferHandle buffer, Penelope.IndexType type, int offsetBytes = 0)
    {
        var buf = _dev.GetBuffer(buffer);
        _dev.Vk.CmdBindIndexBuffer(_cb, buf.Buffer, (ulong)offsetBytes, VulkanConvert.ToVk(type));
    }

    public void SetBindGroup(int index, BindGroupHandle group, ReadOnlySpan<int> dynamicOffsets = default)
    {
        if (_currentPipeline == null) throw new InvalidOperationException("Bind pipeline before bind group.");
        var bg = _dev.GetBindGroup(group);
        var set = bg.Set;
        fixed (int* pOff = dynamicOffsets)
        {
            _dev.Vk.CmdBindDescriptorSets(_cb, PipelineBindPoint.Graphics, _currentPipeline.Layout,
                (uint)index, 1, &set,
                (uint)dynamicOffsets.Length, (uint*)pOff);
        }
    }

    public void SetPushConstants(ShaderStage stages, int offsetBytes, ReadOnlySpan<byte> data)
    {
        if (_currentPipeline == null) throw new InvalidOperationException("Bind pipeline before push constants.");
        fixed (byte* pData = data)
        {
            _dev.Vk.CmdPushConstants(_cb, _currentPipeline.Layout,
                VulkanConvert.ToVk(stages), (uint)offsetBytes, (uint)data.Length, pData);
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
        _dev.Vk.CmdDraw(_cb, (uint)vertexCount, (uint)instanceCount, (uint)firstVertex, (uint)firstInstance);
    }

    public void DrawIndexed(int indexCount, int instanceCount = 1, int firstIndex = 0, int vertexOffset = 0, int firstInstance = 0)
    {
        _dev.Vk.CmdDrawIndexed(_cb, (uint)indexCount, (uint)instanceCount,
            (uint)firstIndex, vertexOffset, (uint)firstInstance);
    }

    public void DrawIndirect(BufferHandle indirectBuffer, int offsetBytes)
    {
        var b = _dev.GetBuffer(indirectBuffer);
        _dev.Vk.CmdDrawIndirect(_cb, b.Buffer, (ulong)offsetBytes, 1, 0);
    }

    public void DrawIndexedIndirect(BufferHandle indirectBuffer, int offsetBytes)
    {
        var b = _dev.GetBuffer(indirectBuffer);
        _dev.Vk.CmdDrawIndexedIndirect(_cb, b.Buffer, (ulong)offsetBytes, 1, 0);
    }

    public void MultiDrawIndirect(BufferHandle indirectBuffer, int offsetBytes, int drawCount, int strideBytes)
    {
        var b = _dev.GetBuffer(indirectBuffer);
        _dev.Vk.CmdDrawIndirect(_cb, b.Buffer, (ulong)offsetBytes, (uint)drawCount, (uint)strideBytes);
    }

    public void MultiDrawIndexedIndirect(BufferHandle indirectBuffer, int offsetBytes, int drawCount, int strideBytes)
    {
        var b = _dev.GetBuffer(indirectBuffer);
        _dev.Vk.CmdDrawIndexedIndirect(_cb, b.Buffer, (ulong)offsetBytes, (uint)drawCount, (uint)strideBytes);
    }

    public void BeginOcclusionQuery(int queryIndex)
    {
        // Requires a pool bound at render-pass begin via RenderPassDesc.OcclusionQuerySet.
        // Wiring pool capture omitted for brevity; no-op if not set up.
    }

    public void EndOcclusionQuery() { }

    public void PushDebugGroup(string name) => VulkanCommandEncoder.DebugPush(_dev, _cb, name);
    public void PopDebugGroup() => VulkanCommandEncoder.DebugPop(_dev, _cb);
    public void InsertDebugMarker(string name) => VulkanCommandEncoder.DebugMarker(_dev, _cb, name);
}

internal sealed unsafe class VulkanComputePassEncoder : IComputePassEncoder
{
    private readonly VulkanDevice _dev;
    private readonly VulkanCommandEncoder _parent;
    private readonly CommandBuffer _cb;
    private VulkanComputePipeline? _currentPipeline;
    private bool _ended;

    public VulkanComputePassEncoder(VulkanDevice dev, VulkanCommandEncoder parent, CommandBuffer cb)
    {
        _dev = dev;
        _parent = parent;
        _cb = cb;
    }

    public void End() { _ended = true; }
    public void Dispose() => End();

    public void SetPipeline(ComputePipelineHandle pipeline)
    {
        _currentPipeline = _dev.GetComputePipeline(pipeline);
        _dev.Vk.CmdBindPipeline(_cb, PipelineBindPoint.Compute, _currentPipeline.Pipeline);
    }

    public void SetBindGroup(int index, BindGroupHandle group, ReadOnlySpan<int> dynamicOffsets = default)
    {
        if (_currentPipeline == null) throw new InvalidOperationException("Bind pipeline before bind group.");
        var bg = _dev.GetBindGroup(group);
        var set = bg.Set;
        fixed (int* pOff = dynamicOffsets)
        {
            _dev.Vk.CmdBindDescriptorSets(_cb, PipelineBindPoint.Compute, _currentPipeline.Layout,
                (uint)index, 1, &set, (uint)dynamicOffsets.Length, (uint*)pOff);
        }
    }

    public void SetPushConstants(ShaderStage stages, int offsetBytes, ReadOnlySpan<byte> data)
    {
        if (_currentPipeline == null) throw new InvalidOperationException("Bind pipeline before push constants.");
        fixed (byte* pData = data)
        {
            _dev.Vk.CmdPushConstants(_cb, _currentPipeline.Layout,
                VulkanConvert.ToVk(stages), (uint)offsetBytes, (uint)data.Length, pData);
        }
    }

    public void Dispatch(int groupsX, int groupsY = 1, int groupsZ = 1)
    {
        _dev.Vk.CmdDispatch(_cb, (uint)groupsX, (uint)groupsY, (uint)groupsZ);
    }

    public void DispatchIndirect(BufferHandle indirectBuffer, int offsetBytes)
    {
        var b = _dev.GetBuffer(indirectBuffer);
        _dev.Vk.CmdDispatchIndirect(_cb, b.Buffer, (ulong)offsetBytes);
    }

    public void PushDebugGroup(string name) => VulkanCommandEncoder.DebugPush(_dev, _cb, name);
    public void PopDebugGroup() => VulkanCommandEncoder.DebugPop(_dev, _cb);
    public void InsertDebugMarker(string name) => VulkanCommandEncoder.DebugMarker(_dev, _cb, name);
}
