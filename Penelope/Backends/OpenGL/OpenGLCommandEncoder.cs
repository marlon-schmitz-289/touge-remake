using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.OpenGL;

namespace Penelope.Backends.OpenGL;

/// <summary>
///     GL command encoder. GL is immediate-mode, so "recording" really just means dispatching the
///     calls in sequence on the current thread. We keep the same Encoder/Pass abstraction the
///     Vulkan backend uses so caller code is identical, but every method here ends in a real GL
///     call rather than a buffered command.
/// </summary>
internal sealed unsafe class OpenGLCommandEncoder : ICommandEncoder
{
    private readonly OpenGLDevice _dev;
    private readonly string? _debugName;
    private bool _disposed;

    public OpenGLCommandEncoder(OpenGLDevice dev, string? debugName)
    {
        _dev = dev;
        _debugName = debugName;
    }

    public void Finish()
    {
        // Nothing to flush — GL has executed everything inline. Hook for future deferral.
    }

    public IRenderPassEncoder BeginRenderPass(in RenderPassDesc desc)
    {
        // Bind / build the FBO this pass writes into. Use FBO 0 for swapchain.
        var (fbo, w, h, isSwapchain) = ResolveFramebuffer(desc);
        _dev.Gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);

        // Per-pass clip-control toggle. Offscreen render targets render with UPPER_LEFT so the
        // round-trip to a sampled texture matches Vulkan (UV (0,0) reads what was at clip y=+1).
        // The default framebuffer renders with LOWER_LEFT so display orientation stays correct
        // (display compositor reads memory bottom-up; UPPER_LEFT would land NDC y=+1 at memory
        // row 0, which the display shows at the bottom = upside-down UI).
        _dev.Gl.ClipControl(
            isSwapchain ? GLEnum.LowerLeft : GLEnum.UpperLeft,
            GLEnum.ZeroToOne);

        // Viewport + scissor default to full attachment area. (Full-area is symmetric, no Y flip
        // needed regardless of clip-control mode.)
        _dev.Gl.Viewport(0, 0, (uint)w, (uint)h);
        _dev.Gl.Scissor(0, 0, (uint)w, (uint)h);
        _dev.Gl.Enable(EnableCap.ScissorTest);

        // Clear color attachments. Stack-alloc the clear-value buffer once outside the loop.
        Span<float> clearScratch = stackalloc float[4];
        for (var i = 0; i < desc.ColorAttachments.Length; i++)
        {
            var a = desc.ColorAttachments[i];
            if (a.Load == LoadOp.Clear)
            {
                clearScratch[0] = a.ClearValue.R;
                clearScratch[1] = a.ClearValue.G;
                clearScratch[2] = a.ClearValue.B;
                clearScratch[3] = a.ClearValue.A;
                fixed (float* p = clearScratch) _dev.Gl.ClearBuffer(GLEnum.Color, i, p);
            }
        }

        if (desc.DepthStencilAttachment.HasValue)
        {
            var ds = desc.DepthStencilAttachment.Value;
            // Depth/stencil writes need to be enabled for ClearBuffer*v to actually clear.
            _dev.Gl.DepthMask(true);
            _dev.Gl.StencilMask(0xFF);
            if (ds.DepthLoad == LoadOp.Clear && ds.StencilLoad == LoadOp.Clear)
            {
                _dev.Gl.ClearBuffer(GLEnum.DepthStencil, 0, ds.DepthClear, (int)ds.StencilClear);
            }
            else
            {
                if (ds.DepthLoad == LoadOp.Clear)
                {
                    var d = ds.DepthClear;
                    _dev.Gl.ClearBuffer(GLEnum.Depth, 0, &d);
                }
                if (ds.StencilLoad == LoadOp.Clear)
                {
                    int s = ds.StencilClear;
                    _dev.Gl.ClearBuffer(GLEnum.Stencil, 0, &s);
                }
            }
        }

        return new OpenGLRenderPassEncoder(_dev, this, fbo, w, h, isSwapchain);
    }

    /// <summary>Look up or create an FBO matching the pass's color/depth attachments.</summary>
    internal (uint fbo, int w, int h, bool isSwapchain) ResolveFramebuffer(in RenderPassDesc desc)
    {
        // Swapchain target shortcut — single color attachment that points at the proxy view.
        if (desc.ColorAttachments.Length == 1 && desc.DepthStencilAttachment is null)
        {
            var v = _dev.GetTextureView(desc.ColorAttachments[0].View);
            var t = _dev.GetTexture(v.TextureId);
            if (t.IsSwapchainProxy)
                return (0, _dev.SwapchainWidth, _dev.SwapchainHeight, true);
        }

        // Cache key: combine all view ids into a stable hash.
        long key = 0;
        for (var i = 0; i < desc.ColorAttachments.Length; i++)
            key = key * 1315423911L ^ (long)desc.ColorAttachments[i].View.Id;
        if (desc.DepthStencilAttachment.HasValue)
            key = key * 1315423911L ^ (long)desc.DepthStencilAttachment.Value.View.Id ^ 0x4d2;

        if (_dev.FboCache.TryGetValue(key, out var fboCached))
        {
            var (cw, ch) = AttachmentSize(desc);
            return (fboCached, cw, ch, false);
        }

        var fbo = _dev.Gl.GenFramebuffer();
        _dev.Gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);

        Span<DrawBufferMode> drawBuffers = stackalloc DrawBufferMode[Math.Max(1, desc.ColorAttachments.Length)];
        for (var i = 0; i < desc.ColorAttachments.Length; i++)
        {
            var v = _dev.GetTextureView(desc.ColorAttachments[i].View);
            var t = _dev.GetTexture(v.TextureId);
            _dev.Gl.FramebufferTexture(FramebufferTarget.Framebuffer,
                FramebufferAttachment.ColorAttachment0 + i,
                _dev.GetViewGlTexture(v), v.BaseMip);
            drawBuffers[i] = DrawBufferMode.ColorAttachment0 + i;
        }
        if (desc.ColorAttachments.Length == 0)
        {
            drawBuffers[0] = DrawBufferMode.None;
        }
        fixed (DrawBufferMode* pdb = drawBuffers)
            _dev.Gl.DrawBuffers((uint)drawBuffers.Length, pdb);

        if (desc.DepthStencilAttachment.HasValue)
        {
            var dv = _dev.GetTextureView(desc.DepthStencilAttachment.Value.View);
            var dt = _dev.GetTexture(dv.TextureId);
            var attach = (dt.Format is TextureFormat.Depth24PlusStencil8 or TextureFormat.Depth32FloatStencil8)
                ? FramebufferAttachment.DepthStencilAttachment
                : FramebufferAttachment.DepthAttachment;
            _dev.Gl.FramebufferTexture(FramebufferTarget.Framebuffer, attach,
                _dev.GetViewGlTexture(dv), dv.BaseMip);
        }

        var status = _dev.Gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
            throw new InvalidOperationException($"FBO incomplete: {status}");

        _dev.FboCache[key] = fbo;
        var (w, h) = AttachmentSize(desc);
        return (fbo, w, h, false);
    }

    private (int w, int h) AttachmentSize(in RenderPassDesc desc)
    {
        if (desc.ColorAttachments.Length > 0)
        {
            var v = _dev.GetTextureView(desc.ColorAttachments[0].View);
            var t = _dev.GetTexture(v.TextureId);
            return (Math.Max(1, t.Width >> v.BaseMip), Math.Max(1, t.Height >> v.BaseMip));
        }
        if (desc.DepthStencilAttachment.HasValue)
        {
            var v = _dev.GetTextureView(desc.DepthStencilAttachment.Value.View);
            var t = _dev.GetTexture(v.TextureId);
            return (Math.Max(1, t.Width >> v.BaseMip), Math.Max(1, t.Height >> v.BaseMip));
        }
        return (_dev.SwapchainWidth, _dev.SwapchainHeight);
    }

    public IComputePassEncoder BeginComputePass(string? debugName = null)
        => new OpenGLComputePassEncoder(_dev, this);

    public void CopyBufferToBuffer(BufferHandle src, int srcOffset, BufferHandle dst, int dstOffset, int sizeBytes)
    {
        var s = _dev.GetBuffer(src);
        var d = _dev.GetBuffer(dst);
        _dev.Gl.BindBuffer(BufferTargetARB.CopyReadBuffer, s.Buffer);
        _dev.Gl.BindBuffer(BufferTargetARB.CopyWriteBuffer, d.Buffer);
        _dev.Gl.CopyBufferSubData(CopyBufferSubDataTarget.CopyReadBuffer, CopyBufferSubDataTarget.CopyWriteBuffer,
            srcOffset, dstOffset, (nuint)sizeBytes);
    }

    public void CopyBufferToTexture(
        BufferHandle src, int srcOffset, int bytesPerRow, int rowsPerImage,
        TextureHandle dst, int mipLevel, int arrayLayer,
        int x, int y, int z, int width, int height, int depth)
    {
        var s = _dev.GetBuffer(src);
        var t = _dev.GetTexture(dst.Id);
        var (_, fmt, type, bpp) = OpenGLConvert.ToGl(t.Format);
        _dev.Gl.BindBuffer(BufferTargetARB.PixelUnpackBuffer, s.Buffer);
        _dev.Gl.PixelStore(PixelStoreParameter.UnpackRowLength, bytesPerRow / Math.Max(1, bpp));
        _dev.Gl.BindTexture(t.Target, t.Texture);
        // Note: this path does NOT row-flip. CopyBufferToTexture is for GPU-only data
        // (e.g. compute pipeline output) which doesn't need the CPU-side row flip.
        switch (t.Dimension)
        {
            case TextureDimension.Tex2D:
                _dev.Gl.TexSubImage2D(t.Target, mipLevel, x, y, (uint)width, (uint)height, fmt, type, (void*)srcOffset);
                break;
            case TextureDimension.Tex2DArray:
            case TextureDimension.Tex3D:
            case TextureDimension.CubeArray:
                _dev.Gl.TexSubImage3D(t.Target, mipLevel, x, y, t.Dimension == TextureDimension.Tex3D ? z : arrayLayer,
                    (uint)width, (uint)height, (uint)depth, fmt, type, (void*)srcOffset);
                break;
            case TextureDimension.Cube:
                var face = TextureTarget.TextureCubeMapPositiveX + arrayLayer;
                _dev.Gl.TexSubImage2D(face, mipLevel, x, y, (uint)width, (uint)height, fmt, type, (void*)srcOffset);
                break;
        }
        _dev.Gl.PixelStore(PixelStoreParameter.UnpackRowLength, 0);
        _dev.Gl.BindBuffer(BufferTargetARB.PixelUnpackBuffer, 0);
    }

    public void CopyTextureToBuffer(
        TextureHandle src, int mipLevel, int arrayLayer,
        int x, int y, int z, int width, int height, int depth,
        BufferHandle dst, int dstOffset, int bytesPerRow, int rowsPerImage)
    {
        var t = _dev.GetTexture(src.Id);
        var b = _dev.GetBuffer(dst);
        var (_, fmt, type, bpp) = OpenGLConvert.ToGl(t.Format);
        _dev.Gl.BindBuffer(BufferTargetARB.PixelPackBuffer, b.Buffer);
        _dev.Gl.PixelStore(PixelStoreParameter.PackRowLength, bytesPerRow / Math.Max(1, bpp));

        // glReadPixels reads from the bound framebuffer, so we attach + read.
        var fbo = _dev.Gl.GenFramebuffer();
        _dev.Gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, fbo);
        _dev.Gl.FramebufferTexture(FramebufferTarget.ReadFramebuffer,
            FramebufferAttachment.ColorAttachment0, t.Texture, mipLevel);
        _dev.Gl.ReadBuffer(ReadBufferMode.ColorAttachment0);
        _dev.Gl.ReadPixels(x, y, (uint)width, (uint)height, fmt, type, (void*)dstOffset);
        _dev.Gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
        _dev.Gl.DeleteFramebuffer(fbo);

        _dev.Gl.PixelStore(PixelStoreParameter.PackRowLength, 0);
        _dev.Gl.BindBuffer(BufferTargetARB.PixelPackBuffer, 0);
    }

    public void CopyTextureToTexture(
        TextureHandle src, int srcMipLevel, int srcArrayLayer, int sx, int sy, int sz,
        TextureHandle dst, int dstMipLevel, int dstArrayLayer, int dx, int dy, int dz,
        int width, int height, int depth)
    {
        var s = _dev.GetTexture(src.Id);
        var d = _dev.GetTexture(dst.Id);
        _dev.Gl.CopyImageSubData(
            s.Texture, ImageTarget(s.Dimension), srcMipLevel, sx, sy, srcArrayLayer + sz,
            d.Texture, ImageTarget(d.Dimension), dstMipLevel, dx, dy, dstArrayLayer + dz,
            (uint)width, (uint)height, (uint)depth);
    }

    private static CopyImageSubDataTarget ImageTarget(TextureDimension d) => d switch
    {
        TextureDimension.Tex2D => CopyImageSubDataTarget.Texture2D,
        TextureDimension.Tex2DArray => CopyImageSubDataTarget.Texture2DArray,
        TextureDimension.Tex3D => CopyImageSubDataTarget.Texture3D,
        TextureDimension.Cube => CopyImageSubDataTarget.TextureCubeMap,
        TextureDimension.CubeArray => CopyImageSubDataTarget.TextureCubeMapArray,
        _ => CopyImageSubDataTarget.Texture2D,
    };

    public void GenerateMipmaps(TextureHandle texture)
    {
        var t = _dev.GetTexture(texture.Id);
        _dev.Gl.BindTexture(t.Target, t.Texture);
        _dev.Gl.GenerateMipmap(t.Target);
    }

    public void MakeTextureSampleable(TextureViewHandle view)
    {
        // glMemoryBarrier for image loads/stores — for sampling after a render pass write we
        // additionally need the texture-fetch barrier so subsequent shader fetches see the data.
        _dev.Gl.MemoryBarrier(MemoryBarrierMask.TextureFetchBarrierBit
                              | MemoryBarrierMask.FramebufferBarrierBit);
    }

    public void WriteTimestamp(QueryPoolHandle pool, int queryIndex)
    {
        var p = _dev.GetQueryPool(pool);
        _dev.Gl.QueryCounter(p.Queries[queryIndex], QueryCounterTarget.Timestamp);
    }

    public void ResetQueryPool(QueryPoolHandle pool, int firstQuery, int queryCount)
    {
        // GL queries auto-reset on next BeginQuery/QueryCounter — no explicit reset needed.
    }

    public void ResolveQueryData(QueryPoolHandle pool, int firstQuery, int queryCount, BufferHandle dst, int dstOffset)
    {
        var p = _dev.GetQueryPool(pool);
        var b = _dev.GetBuffer(dst);
        _dev.Gl.BindBuffer(BufferTargetARB.CopyWriteBuffer, b.Buffer);
        for (var i = 0; i < queryCount; i++)
        {
            ulong v = 0;
            _dev.Gl.GetQueryObject(p.Queries[firstQuery + i], QueryObjectParameterName.Result, &v);
            _dev.Gl.BufferSubData(BufferTargetARB.CopyWriteBuffer, dstOffset + i * sizeof(ulong),
                sizeof(ulong), &v);
        }
    }

    public void PushDebugGroup(string name)
    {
        try { _dev.Gl.PushDebugGroup(DebugSource.DebugSourceApplication, 0, (uint)name.Length, name); }
        catch { /* KHR_debug not available */ }
    }
    public void PopDebugGroup()
    {
        try { _dev.Gl.PopDebugGroup(); } catch { }
    }
    public void InsertDebugMarker(string name)
    {
        try { _dev.Gl.DebugMessageInsert(DebugSource.DebugSourceApplication, DebugType.DebugTypeMarker,
            0, DebugSeverity.DebugSeverityNotification, (uint)name.Length, name); }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Finish();
    }
}

internal sealed unsafe class OpenGLRenderPassEncoder : IRenderPassEncoder
{
    private readonly OpenGLDevice _dev;
    private readonly OpenGLCommandEncoder _parent;
    private readonly uint _fbo;
    private readonly int _width, _height;
    private readonly bool _isSwapchain;
    private OpenGLRenderPipeline? _pipeline;
    private bool _ended;

    /// <summary>Pending vertex-buffer bindings — applied at draw time once we know the pipeline (and therefore strides).</summary>
    private readonly (uint glBuffer, int offset)[] _pendingVertex = new (uint, int)[16];
    private uint _pendingVertexMask; // bit i = slot i has a buffer waiting to be bound
    /// <summary>Slots that have ever been bound this encoder — re-applied on each pipeline change since the new VAO has empty state.</summary>
    private uint _everBoundVertexMask;

    public OpenGLRenderPassEncoder(OpenGLDevice dev, OpenGLCommandEncoder parent, uint fbo, int w, int h, bool isSwapchain)
    {
        _dev = dev;
        _parent = parent;
        _fbo = fbo;
        _width = w;
        _height = h;
        _isSwapchain = isSwapchain;
        // Swapchain passes run with glClipControl(LOWER_LEFT) so the display orientation is
        // correct (memory row 0 → display top). But that also flips glViewport/glScissor's
        // y-origin to the bottom of the framebuffer — and the engine passes coordinates with
        // y=0 at the top (Vulkan convention). So for swapchain passes we flip y on viewport/
        // scissor calls to bridge the two. Offscreen RT passes use UPPER_LEFT, where the
        // engine's coordinate convention matches GL natively, so no flip there.
    }

    public void End()
    {
        if (_ended) return;
        _ended = true;
        // Disable scissor on exit so any subsequent state setup outside passes behaves predictably.
        _dev.Gl.Disable(EnableCap.ScissorTest);
    }

    public void Dispose() => End();

    public void SetViewport(float x, float y, float width, float height, float minDepth = 0f, float maxDepth = 1f)
    {
        // Normalize Vulkan-style negative dimensions (the engine uses VK_EXT_negative_viewport_height
        // to flip Y for Vulkan output; on GL we just take the absolute region). glViewport rejects
        // negative width/height with GL_INVALID_VALUE.
        if (height < 0) { y += height; height = -height; }
        if (width < 0)  { x += width;  width = -width; }
        // Swapchain pass uses LOWER_LEFT clip control — flip y from top-down (engine convention)
        // to bottom-up (GL native). Offscreen RT pass uses UPPER_LEFT — pass through.
        var glY = _isSwapchain ? _height - ((int)y + (int)height) : (int)y;
        _dev.Gl.Viewport((int)x, glY, (uint)width, (uint)height);
        _dev.Gl.DepthRange(minDepth, maxDepth);
    }

    public void SetScissor(int x, int y, int width, int height)
    {
        if (height < 0) { y += height; height = -height; }
        if (width < 0)  { x += width;  width = -width; }
        var glY = _isSwapchain ? _height - (y + height) : y;
        _dev.Gl.Scissor(x, glY, (uint)width, (uint)height);
    }

    public void SetBlendConstant(float r, float g, float b, float a)
    {
        _dev.Gl.BlendColor(r, g, b, a);
    }

    public void SetStencilReference(uint reference)
    {
        if (_pipeline == null) return;
        var ds = _pipeline.Desc.DepthStencil;
        _dev.Gl.StencilFuncSeparate(TriangleFace.Front,
            OpenGLConvert.ToGlStencil(ds.StencilFront.Compare), (int)reference, ds.StencilReadMask);
        _dev.Gl.StencilFuncSeparate(TriangleFace.Back,
            OpenGLConvert.ToGlStencil(ds.StencilBack.Compare), (int)reference, ds.StencilReadMask);
    }

    public void SetPipeline(RenderPipelineHandle pipeline)
    {
        _pipeline = _dev.GetRenderPipeline(pipeline);
        _dev.Gl.UseProgram(_pipeline.Program);
        _dev.Gl.BindVertexArray(_pipeline.Vao);
        // Vertex/index bindings are per-VAO state in GL; the new VAO has fresh state, so
        // re-mark everything dirty so the next draw re-attaches the active buffers.
        _activeIndexBufferDirty = _activeIndexBuffer != 0;
        _pendingVertexMask |= _everBoundVertexMask;

        // Apply pipeline state: rasterizer, depth/stencil, blend, multisample.
        var raster = _pipeline.Desc.Rasterizer;
        if (raster.Cull == CullMode.None) _dev.Gl.Disable(EnableCap.CullFace);
        else
        {
            _dev.Gl.Enable(EnableCap.CullFace);
            _dev.Gl.CullFace(OpenGLConvert.ToGl(raster.Cull));
        }
        _dev.Gl.FrontFace(OpenGLConvert.ToGl(raster.FrontFace));
        _dev.Gl.PolygonMode(TriangleFace.FrontAndBack, OpenGLConvert.ToGl(raster.Polygon));
        if (raster.DepthClampEnabled) _dev.Gl.Enable(EnableCap.DepthClamp);
        else _dev.Gl.Disable(EnableCap.DepthClamp);

        var ds = _pipeline.Desc.DepthStencil;
        if (ds.DepthTestEnabled) _dev.Gl.Enable(EnableCap.DepthTest);
        else _dev.Gl.Disable(EnableCap.DepthTest);
        _dev.Gl.DepthFunc(OpenGLConvert.ToGlDepth(ds.DepthCompare));
        _dev.Gl.DepthMask(ds.DepthWriteEnabled);
        if (ds.StencilEnabled)
        {
            _dev.Gl.Enable(EnableCap.StencilTest);
            ApplyStencilFace(TriangleFace.Front, ds.StencilFront, ds.StencilReadMask, ds.StencilWriteMask);
            ApplyStencilFace(TriangleFace.Back, ds.StencilBack, ds.StencilReadMask, ds.StencilWriteMask);
        }
        else _dev.Gl.Disable(EnableCap.StencilTest);

        if (ds.DepthBiasConstant != 0 || ds.DepthBiasSlope != 0)
        {
            _dev.Gl.Enable(EnableCap.PolygonOffsetFill);
            _dev.Gl.PolygonOffset(ds.DepthBiasSlope, ds.DepthBiasConstant);
        }
        else _dev.Gl.Disable(EnableCap.PolygonOffsetFill);

        // Per-attachment blend state.
        for (var i = 0; i < _pipeline.Desc.ColorTargets.Length; i++)
        {
            var b = _pipeline.Desc.ColorTargets[i].Blend;
            uint idx = (uint)i;
            if (b.Enabled) _dev.Gl.Enable(EnableCap.Blend, idx);
            else _dev.Gl.Disable(EnableCap.Blend, idx);
            _dev.Gl.BlendFuncSeparate(idx,
                OpenGLConvert.ToGl(b.SrcColor), OpenGLConvert.ToGl(b.DstColor),
                OpenGLConvert.ToGl(b.SrcAlpha), OpenGLConvert.ToGl(b.DstAlpha));
            _dev.Gl.BlendEquationSeparate(idx,
                OpenGLConvert.ToGl(b.ColorOp), OpenGLConvert.ToGl(b.AlphaOp));
            _dev.Gl.ColorMask(idx,
                (b.WriteMask & ColorWriteMask.R) != 0,
                (b.WriteMask & ColorWriteMask.G) != 0,
                (b.WriteMask & ColorWriteMask.B) != 0,
                (b.WriteMask & ColorWriteMask.A) != 0);
        }

        if (_pipeline.Desc.Multisample.SampleCount > 1) _dev.Gl.Enable(EnableCap.Multisample);
        else _dev.Gl.Disable(EnableCap.Multisample);
        if (_pipeline.Desc.Multisample.AlphaToCoverageEnabled) _dev.Gl.Enable(EnableCap.SampleAlphaToCoverage);
        else _dev.Gl.Disable(EnableCap.SampleAlphaToCoverage);

        // Push-constant UBO is bound at SetPushConstants time (after data is staged); no-op here.
    }

    private void ApplyStencilFace(TriangleFace face, StencilFaceState s, byte readMask, byte writeMask)
    {
        _dev.Gl.StencilFuncSeparate(face, OpenGLConvert.ToGlStencil(s.Compare), 0, readMask);
        _dev.Gl.StencilOpSeparate(face,
            OpenGLConvert.ToGl(s.FailOp),
            OpenGLConvert.ToGl(s.DepthFailOp),
            OpenGLConvert.ToGl(s.PassOp));
        _dev.Gl.StencilMaskSeparate(face, writeMask);
    }

    public void SetVertexBuffer(int slot, BufferHandle buffer, int offsetBytes = 0)
    {
        // Vulkan allows binding vertex buffers before the pipeline; defer the GL bind until
        // a draw call so the stride (which lives on the pipeline) is known.
        var buf = _dev.GetBuffer(buffer);
        _pendingVertex[slot] = (buf.Buffer, offsetBytes);
        _pendingVertexMask |= 1u << slot;
        _everBoundVertexMask |= 1u << slot;
    }

    private void FlushPendingVertexBindings()
    {
        if (_pendingVertexMask == 0 || _pipeline == null) return;
        var mask = _pendingVertexMask;
        for (var slot = 0; mask != 0; slot++, mask >>= 1)
        {
            if ((mask & 1) == 0) continue;
            var stride = 0;
            foreach (var b in _pipeline.Desc.VertexLayout.Buffers)
                if (b.BufferSlot == slot) { stride = b.StrideBytes; break; }
            var (glBuf, off) = _pendingVertex[slot];
            _dev.Gl.BindVertexBuffer((uint)slot, glBuf, off, (uint)stride);
        }
        _pendingVertexMask = 0;
    }

    public void SetIndexBuffer(BufferHandle buffer, IndexType type, int offsetBytes = 0)
    {
        // GL_ELEMENT_ARRAY_BUFFER binding is per-VAO. We track the most recently-set IBO so
        // every pipeline switch (which swaps the VAO) re-attaches it before the next draw.
        var buf = _dev.GetBuffer(buffer);
        _activeIndexBuffer = buf.Buffer;
        _activeIndexBufferDirty = true;
        _currentIndexType = OpenGLConvert.ToGl(type);
        _currentIndexOffset = offsetBytes;
        _currentIndexSize = type == IndexType.UInt16 ? 2 : 4;
    }

    private DrawElementsType _currentIndexType = DrawElementsType.UnsignedInt;
    private int _currentIndexOffset;
    private int _currentIndexSize = 4;
    private uint _activeIndexBuffer;
    /// <summary>Set true on SetIndexBuffer or SetPipeline; cleared after the bind goes through.</summary>
    private bool _activeIndexBufferDirty;

    private void FlushPendingIndexBuffer()
    {
        if (!_activeIndexBufferDirty || _pipeline == null || _activeIndexBuffer == 0) return;
        _dev.Gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _activeIndexBuffer);
        _activeIndexBufferDirty = false;
    }

    public void SetBindGroup(int index, BindGroupHandle group, ReadOnlySpan<int> dynamicOffsets = default)
        => OpenGLBinder.BindGroup(_dev, index, group, dynamicOffsets);

    public void SetPushConstants(ShaderStage stages, int offsetBytes, ReadOnlySpan<byte> data)
        => OpenGLBinder.StagePushConstants(_dev, offsetBytes, data);

    public void SetPushConstantMatrix4(ShaderStage stages, int offsetBytes, in Matrix4x4 value)
    {
        var m = value;
        var span = new ReadOnlySpan<byte>(&m, sizeof(Matrix4x4));
        SetPushConstants(stages, offsetBytes, span);
    }

    public void Draw(int vertexCount, int instanceCount = 1, int firstVertex = 0, int firstInstance = 0)
    {
        if (_pipeline == null) return;
        FlushPendingVertexBindings();
        FlushPendingIndexBuffer();
        if (firstInstance == 0)
        {
            _dev.Gl.DrawArraysInstanced(_pipeline.Topology, firstVertex, (uint)vertexCount, (uint)instanceCount);
        }
        else
        {
            _dev.Gl.DrawArraysInstancedBaseInstance(_pipeline.Topology, firstVertex,
                (uint)vertexCount, (uint)instanceCount, (uint)firstInstance);
        }
    }

    public void DrawIndexed(int indexCount, int instanceCount = 1, int firstIndex = 0, int vertexOffset = 0, int firstInstance = 0)
    {
        if (_pipeline == null) return;
        FlushPendingVertexBindings();
        FlushPendingIndexBuffer();
        var indexOffset = (nuint)(_currentIndexOffset + firstIndex * _currentIndexSize);
        if (vertexOffset == 0 && firstInstance == 0)
        {
            _dev.Gl.DrawElementsInstanced(_pipeline.Topology, (uint)indexCount, _currentIndexType,
                (void*)indexOffset, (uint)instanceCount);
        }
        else
        {
            _dev.Gl.DrawElementsInstancedBaseVertexBaseInstance(_pipeline.Topology, (uint)indexCount,
                _currentIndexType, (void*)indexOffset, (uint)instanceCount, vertexOffset, (uint)firstInstance);
        }
    }

    public void DrawIndirect(BufferHandle indirectBuffer, int offsetBytes)
    {
        if (_pipeline == null) return;
        FlushPendingVertexBindings();
        FlushPendingIndexBuffer();
        var b = _dev.GetBuffer(indirectBuffer);
        _dev.Gl.BindBuffer(BufferTargetARB.DrawIndirectBuffer, b.Buffer);
        _dev.Gl.DrawArraysIndirect(_pipeline.Topology, (void*)offsetBytes);
    }

    public void DrawIndexedIndirect(BufferHandle indirectBuffer, int offsetBytes)
    {
        if (_pipeline == null) return;
        FlushPendingVertexBindings();
        FlushPendingIndexBuffer();
        var b = _dev.GetBuffer(indirectBuffer);
        _dev.Gl.BindBuffer(BufferTargetARB.DrawIndirectBuffer, b.Buffer);
        _dev.Gl.DrawElementsIndirect(_pipeline.Topology, _currentIndexType, (void*)offsetBytes);
    }

    public void MultiDrawIndirect(BufferHandle indirectBuffer, int offsetBytes, int drawCount, int strideBytes)
    {
        if (_pipeline == null) return;
        FlushPendingVertexBindings();
        FlushPendingIndexBuffer();
        var b = _dev.GetBuffer(indirectBuffer);
        _dev.Gl.BindBuffer(BufferTargetARB.DrawIndirectBuffer, b.Buffer);
        _dev.Gl.MultiDrawArraysIndirect(_pipeline.Topology, (void*)offsetBytes, (uint)drawCount, (uint)strideBytes);
    }

    public void MultiDrawIndexedIndirect(BufferHandle indirectBuffer, int offsetBytes, int drawCount, int strideBytes)
    {
        if (_pipeline == null) return;
        FlushPendingVertexBindings();
        FlushPendingIndexBuffer();
        var b = _dev.GetBuffer(indirectBuffer);
        _dev.Gl.BindBuffer(BufferTargetARB.DrawIndirectBuffer, b.Buffer);
        _dev.Gl.MultiDrawElementsIndirect(_pipeline.Topology, _currentIndexType,
            (void*)offsetBytes, (uint)drawCount, (uint)strideBytes);
    }

    public void BeginOcclusionQuery(int queryIndex)
    {
        // Penelope binds the occlusion-query pool at render-pass-begin via RenderPassDesc.OcclusionQuerySet —
        // not currently propagated to this encoder. Hooked up here once we wire that pipeline.
    }
    public void EndOcclusionQuery() { }

    public void PushDebugGroup(string name) => _parent.PushDebugGroup(name);
    public void PopDebugGroup() => _parent.PopDebugGroup();
    public void InsertDebugMarker(string name) => _parent.InsertDebugMarker(name);
}

internal sealed unsafe class OpenGLComputePassEncoder : IComputePassEncoder
{
    private readonly OpenGLDevice _dev;
    private readonly OpenGLCommandEncoder _parent;
    private OpenGLComputePipeline? _pipeline;
    private bool _ended;

    public OpenGLComputePassEncoder(OpenGLDevice dev, OpenGLCommandEncoder parent)
    {
        _dev = dev;
        _parent = parent;
    }

    public void End() { _ended = true; }
    public void Dispose() => End();

    public void SetPipeline(ComputePipelineHandle pipeline)
    {
        _pipeline = _dev.GetComputePipeline(pipeline);
        _dev.Gl.UseProgram(_pipeline.Program);
    }

    public void SetBindGroup(int index, BindGroupHandle group, ReadOnlySpan<int> dynamicOffsets = default)
        => OpenGLBinder.BindGroup(_dev, index, group, dynamicOffsets);

    public void SetPushConstants(ShaderStage stages, int offsetBytes, ReadOnlySpan<byte> data)
        => OpenGLBinder.StagePushConstants(_dev, offsetBytes, data);

    public void Dispatch(int groupsX, int groupsY = 1, int groupsZ = 1)
    {
        _dev.Gl.DispatchCompute((uint)groupsX, (uint)groupsY, (uint)groupsZ);
        // Default barrier — caller can be more specific via MakeTextureSampleable.
        _dev.Gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit
                              | MemoryBarrierMask.TextureFetchBarrierBit
                              | MemoryBarrierMask.UniformBarrierBit);
    }

    public void DispatchIndirect(BufferHandle indirectBuffer, int offsetBytes)
    {
        var b = _dev.GetBuffer(indirectBuffer);
        _dev.Gl.BindBuffer(BufferTargetARB.DispatchIndirectBuffer, b.Buffer);
        _dev.Gl.DispatchComputeIndirect(offsetBytes);
        _dev.Gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit | MemoryBarrierMask.TextureFetchBarrierBit);
    }

    public void PushDebugGroup(string name) => _parent.PushDebugGroup(name);
    public void PopDebugGroup() => _parent.PopDebugGroup();
    public void InsertDebugMarker(string name) => _parent.InsertDebugMarker(name);
}

/// <summary>
///     Shared bind-group / push-constant resolution for both the render and compute pass encoders.
///     Previously these two paths were hand-copied and had already drifted: the render path bound
///     storage textures via <c>BindTexture</c> (sampled-texture semantics) instead of
///     <c>BindImageTexture</c>, a latent image-load/store bug. Unifying here uses the correct
///     storage-image path for both.
///
///     NOTE: the bind-group set <c>index</c> is intentionally not yet remapped to a flat GL unit —
///     every call site binds set 0, so resources go to their raw <c>entry.Binding</c>. True
///     multi-set support needs a (set,binding)->GL-unit map captured from shader reflection.
/// </summary>
internal static unsafe class OpenGLBinder
{
    public static void BindGroup(OpenGLDevice dev, int set, BindGroupHandle group, ReadOnlySpan<int> dynamicOffsets)
    {
        // The GL backend binds resources at their raw binding within a single descriptor set.
        // Multi-set support needs a (set,binding)->flat-GL-unit remap captured from SPIRV-Cross at
        // shader-translation time (set>0 would otherwise alias set 0 and stomp the push-constant
        // UBO). Until that lands, reject set>0 LOUDLY rather than silently corrupting bindings.
        if (set != 0)
            throw new NotSupportedException(
                $"OpenGL backend supports only bind-group set 0 (got set {set}). Multi-set binding " +
                "requires a (set,binding)->GL-unit reflection remap that is not yet implemented; " +
                "Vulkan/Metal support multiple sets natively.");

        var bg = dev.GetBindGroup(group);
        var layout = dev.GetBindGroupLayout(bg.LayoutId);
        var dynIdx = 0;
        foreach (var entry in bg.Entries)
        {
            var le = FindLayoutEntry(layout, entry.Binding);
            switch (le.Type)
            {
                case BindingType.UniformBuffer:
                {
                    var buf = dev.GetBuffer(entry.Buffer);
                    var off = entry.BufferOffset + (le.HasDynamicOffset && dynIdx < dynamicOffsets.Length
                        ? dynamicOffsets[dynIdx++] : 0);
                    var size = entry.BufferSize > 0 ? entry.BufferSize : buf.SizeBytes - off;
                    dev.Gl.BindBufferRange(BufferTargetARB.UniformBuffer, (uint)entry.Binding,
                        buf.Buffer, off, (nuint)size);
                    break;
                }
                case BindingType.StorageBuffer:
                case BindingType.ReadOnlyStorageBuffer:
                {
                    var buf = dev.GetBuffer(entry.Buffer);
                    var off = entry.BufferOffset + (le.HasDynamicOffset && dynIdx < dynamicOffsets.Length
                        ? dynamicOffsets[dynIdx++] : 0);
                    var size = entry.BufferSize > 0 ? entry.BufferSize : buf.SizeBytes - off;
                    dev.Gl.BindBufferRange(BufferTargetARB.ShaderStorageBuffer, (uint)entry.Binding,
                        buf.Buffer, off, (nuint)size);
                    break;
                }
                case BindingType.SampledTexture:
                {
                    var view = dev.GetTextureView(entry.TextureView);
                    var tex = dev.GetTexture(view.TextureId);
                    dev.Gl.ActiveTexture(TextureUnit.Texture0 + entry.Binding);
                    dev.Gl.BindTexture(tex.Target, dev.GetViewGlTexture(view));
                    break;
                }
                case BindingType.StorageTexture:
                case BindingType.ReadOnlyStorageTexture:
                {
                    var view = dev.GetTextureView(entry.TextureView);
                    var (internalFmt, _, _, _) = OpenGLConvert.ToGl(view.Format);
                    dev.Gl.BindImageTexture((uint)entry.Binding, dev.GetViewGlTexture(view),
                        view.BaseMip, view.LayerCount > 1, view.BaseLayer,
                        le.Type == BindingType.ReadOnlyStorageTexture ? BufferAccessARB.ReadOnly : BufferAccessARB.ReadWrite,
                        internalFmt);
                    break;
                }
                case BindingType.Sampler:
                case BindingType.ComparisonSampler:
                {
                    var samp = dev.GetSampler(entry.Sampler);
                    dev.Gl.BindSampler((uint)entry.Binding, samp.Sampler);
                    break;
                }
                case BindingType.CombinedImageSampler:
                {
                    var view = dev.GetTextureView(entry.TextureView);
                    var tex = dev.GetTexture(view.TextureId);
                    var samp = dev.GetSampler(entry.Sampler);
                    dev.Gl.ActiveTexture(TextureUnit.Texture0 + entry.Binding);
                    dev.Gl.BindTexture(tex.Target, dev.GetViewGlTexture(view));
                    dev.Gl.BindSampler((uint)entry.Binding, samp.Sampler);
                    break;
                }
            }
        }
    }

    public static void StagePushConstants(OpenGLDevice dev, int offsetBytes, ReadOnlySpan<byte> data)
    {
        // Stage into the device-wide push-constant UBO. Re-bind it here so it survives pipeline
        // switches without depending on bind-group state. Only the bytes that actually changed are
        // uploaded (callers typically write ~80 of the 256 reserved bytes).
        if (offsetBytes + data.Length > OpenGLDevice.PushConstantBufferSize)
            throw new ArgumentException(
                $"Push constants exceed reserved size ({OpenGLDevice.PushConstantBufferSize} bytes).");
        data.CopyTo(dev.PushConstantStaging.AsSpan(offsetBytes));
        dev.Gl.BindBuffer(BufferTargetARB.UniformBuffer, dev.PushConstantUbo);
        fixed (byte* p = dev.PushConstantStaging)
            dev.Gl.BufferSubData(BufferTargetARB.UniformBuffer, offsetBytes, (nuint)data.Length, p + offsetBytes);
        // The indexed binding persists for the context's lifetime — bind once, then skip.
        if (!dev.PushConstantRangeBound)
        {
            dev.Gl.BindBufferRange(BufferTargetARB.UniformBuffer, (uint)ShaderLib.PushConstantBinding,
                dev.PushConstantUbo, 0, OpenGLDevice.PushConstantBufferSize);
            dev.PushConstantRangeBound = true;
        }
    }

    private static BindGroupLayoutEntry FindLayoutEntry(OpenGLBindGroupLayout layout, int binding)
    {
        foreach (var e in layout.Entries) if (e.Binding == binding) return e;
        throw new ArgumentException($"Binding {binding} not found in bind-group layout.");
    }
}
