using System.Runtime.Versioning;
using SharpMetal.Foundation;
using SharpMetal.Metal;

namespace Penelope.Backends.Metal;

[SupportedOSPlatform("macos")]
public sealed unsafe partial class MetalDevice
{
    // ---- Buffers ----

    public BufferHandle CreateBuffer(in BufferDesc desc, ReadOnlySpan<byte> initialData = default)
    {
        // Storage mode picks where the buffer lives: Shared = CPU+GPU coherent (slow on
        // discrete, ideal on Apple Silicon's unified memory), Private = GPU-only (no map).
        var hostVisible = desc.Access != BufferAccess.Immutable;
        var options = hostVisible
            ? MTLResourceOptions.ResourceStorageModeShared
            : MTLResourceOptions.ResourceStorageModePrivate;

        MTLBuffer buf;
        if (!initialData.IsEmpty && hostVisible)
        {
            // Direct upload via NewBuffer(pointer, length, options) — copies into a fresh
            // shared-memory allocation in one call.
            fixed (byte* p = initialData)
                buf = Device.NewBuffer((nint)p, (ulong)desc.SizeBytes, options);
        }
        else
        {
            buf = Device.NewBuffer((ulong)desc.SizeBytes, options);
            if (!initialData.IsEmpty)
            {
                // Private storage — stage through a shared scratch buffer + blit copy.
                using var staging = Device.NewBuffer((ulong)initialData.Length,
                    MTLResourceOptions.ResourceStorageModeShared);
                fixed (byte* src = initialData)
                    System.Buffer.MemoryCopy(src, staging.Contents.ToPointer(),
                        initialData.Length, initialData.Length);
                var cb = Queue.CommandBuffer();
                var blit = cb.BlitCommandEncoder();
                blit.CopyFromBuffer(staging, 0, buf, 0, (ulong)initialData.Length);
                blit.EndEncoding();
                cb.Commit();
                cb.WaitUntilCompleted();
            }
        }

        var wrapper = new MetalBuffer
        {
            Buffer = buf,
            SizeBytes = desc.SizeBytes,
            Usage = desc.Usage,
            Access = desc.Access,
            HostVisible = hostVisible,
        };
        var id = NewHandleId();
        _buffers[id] = wrapper;
        return new BufferHandle(id);
    }

    public void WriteBuffer(BufferHandle buffer, int offsetBytes, ReadOnlySpan<byte> data)
    {
        var buf = _buffers[buffer.Id];
        if (!buf.HostVisible)
            throw new InvalidOperationException(
                "WriteBuffer on a private buffer: stage through a Shared buffer + CopyBufferToBuffer.");
        fixed (byte* src = data)
            System.Buffer.MemoryCopy(src, (byte*)buf.Buffer.Contents.ToPointer() + offsetBytes,
                data.Length, data.Length);
    }

    public Span<byte> MapBuffer(BufferHandle buffer, int offsetBytes, int sizeBytes)
    {
        var buf = _buffers[buffer.Id];
        if (!buf.HostVisible)
            throw new InvalidOperationException("MapBuffer: buffer is GPU-private.");
        return new Span<byte>((byte*)buf.Buffer.Contents.ToPointer() + offsetBytes, sizeBytes);
    }

    public void UnmapBuffer(BufferHandle buffer)
    {
        // Shared buffers are persistently CPU-visible; nothing to do.
    }

    public void DestroyBuffer(BufferHandle buffer)
    {
        if (!_buffers.Remove(buffer.Id, out var b)) return;
        b.Buffer.Dispose();
    }

    // ---- Textures ----

    public TextureHandle CreateTexture(in TextureDesc desc, ReadOnlySpan<byte> initialData = default)
    {
        var pixelFmt = MetalConvert.ToMetal(desc.Format);
        // Plain var (not using) — descriptor properties are setters and Roslyn forbids mutating
        // a `using` variable. The descriptor releases via the autorelease pool.
        var td = MTLTextureDescriptor.Texture2DDescriptor(pixelFmt,
            (ulong)desc.Width, (ulong)desc.Height, desc.MipLevels > 1);
        td.TextureType = desc.SampleCount > 1 ? MTLTextureType.Type2DMultisample : MetalConvert.ToMetal(desc.Dimension);
        td.Width = (ulong)desc.Width;
        td.Height = (ulong)desc.Height;
        td.Depth = (ulong)desc.Depth;
        td.MipmapLevelCount = (ulong)desc.MipLevels;
        td.ArrayLength = (ulong)desc.ArrayLayers;
        td.SampleCount = (ulong)desc.SampleCount;
        td.Usage = MetalConvert.ToMetal(desc.Usage);
        td.StorageMode = (desc.Usage & TextureUsage.DepthStencilAttachment) != 0
                         || (desc.Usage & TextureUsage.ColorAttachment) != 0
            ? MTLStorageMode.Private
            : MTLStorageMode.Private;

        var tex = Device.NewTexture(td);
        td.Dispose();
        var wrapper = new MetalTexture
        {
            Texture = tex,
            Format = desc.Format,
            Width = desc.Width,
            Height = desc.Height,
            Depth = desc.Depth,
            MipLevels = desc.MipLevels,
            ArrayLayers = desc.ArrayLayers,
            SampleCount = desc.SampleCount,
            Usage = desc.Usage,
            Dimension = desc.Dimension,
        };

        var id = NewHandleId();
        _textures[id] = wrapper;

        var defView = new MetalTextureView
        {
            TextureId = id,
            Format = desc.Format,
            Aspect = TextureAspect.All,
            BaseMip = 0,
            MipCount = desc.MipLevels,
            BaseLayer = 0,
            LayerCount = desc.ArrayLayers,
        };
        var defViewId = NewHandleId();
        _textureViews[defViewId] = defView;
        wrapper.DefaultViewId = defViewId;

        if (!initialData.IsEmpty)
        {
            WriteTexture(new TextureHandle(id), 0, 0, 0, 0, 0,
                desc.Width, desc.Height, desc.Depth, initialData,
                desc.Width * MetalConvert.BytesPerPixel(desc.Format), desc.Height);
        }
        return new TextureHandle(id);
    }

    public void WriteTexture(
        TextureHandle texture,
        int mipLevel, int arrayLayer,
        int x, int y, int z,
        int width, int height, int depth,
        ReadOnlySpan<byte> data,
        int bytesPerRow, int rowsPerImage)
    {
        var tex = _textures[texture.Id];
        var region = new MTLRegion
        {
            origin = new MTLOrigin { x = (ulong)x, y = (ulong)y, z = (ulong)z },
            size = new MTLSize { width = (ulong)width, height = (ulong)height, depth = (ulong)depth },
        };
        // For Private-storage textures (default), MTLTexture.ReplaceRegion is not allowed —
        // route through a temporary Shared buffer + BlitCommandEncoder.
        using var staging = Device.NewBuffer((ulong)data.Length, MTLResourceOptions.ResourceStorageModeShared);
        fixed (byte* src = data)
            System.Buffer.MemoryCopy(src, staging.Contents.ToPointer(), data.Length, data.Length);

        var cb = Queue.CommandBuffer();
        var blit = cb.BlitCommandEncoder();
        blit.CopyFromBuffer(
            sourceBuffer: staging,
            sourceOffset: 0,
            sourceBytesPerRow: (ulong)bytesPerRow,
            sourceBytesPerImage: (ulong)(bytesPerRow * rowsPerImage),
            sourceSize: region.size,
            destinationTexture: tex.Texture,
            destinationSlice: (ulong)arrayLayer,
            destinationLevel: (ulong)mipLevel,
            destinationOrigin: region.origin);
        blit.EndEncoding();
        // no wait: the queue runs the blit before any later draw, and the command buffer keeps staging and texture alive
        // (a wait per call made a car's ~400 mip writes cost ~75 ms)
        cb.Commit();
    }

    public void DestroyTexture(TextureHandle texture)
    {
        if (!_textures.Remove(texture.Id, out var t)) return;
        if (t.DefaultViewId != 0 && _textureViews.Remove(t.DefaultViewId, out var defView))
            if (defView.HasViewTexture) defView.ViewTexture.Dispose();
        if (!t.IsSwapchainProxy) t.Texture.Dispose();
    }

    public TextureViewHandle DefaultTextureView(TextureHandle texture)
        => new(_textures[texture.Id].DefaultViewId);

    public TextureViewHandle CreateTextureView(TextureHandle texture, in TextureViewDesc desc)
    {
        var tex = _textures[texture.Id];
        var fmt = desc.Format ?? tex.Format;
        var mipCount = desc.MipLevelCount == 0 ? tex.MipLevels - desc.BaseMipLevel : desc.MipLevelCount;
        var layerCount = desc.ArrayLayerCount == 0 ? tex.ArrayLayers - desc.BaseArrayLayer : desc.ArrayLayerCount;

        var view = new MetalTextureView
        {
            TextureId = texture.Id,
            Format = fmt,
            Aspect = desc.Aspect,
            BaseMip = desc.BaseMipLevel,
            MipCount = mipCount,
            BaseLayer = desc.BaseArrayLayer,
            LayerCount = layerCount,
        };
        if (desc.BaseMipLevel != 0 || mipCount != tex.MipLevels ||
            desc.BaseArrayLayer != 0 || layerCount != tex.ArrayLayers ||
            (desc.Format.HasValue && desc.Format.Value != tex.Format))
        {
            view.ViewTexture = tex.Texture.NewTextureView(
                MetalConvert.ToMetal(fmt),
                MetalConvert.ToMetal(desc.Dimension ?? tex.Dimension),
                new NSRange { location = (ulong)desc.BaseMipLevel, length = (ulong)mipCount },
                new NSRange { location = (ulong)desc.BaseArrayLayer, length = (ulong)layerCount });
            view.HasViewTexture = true;
        }
        var id = NewHandleId();
        _textureViews[id] = view;
        return new TextureViewHandle(id);
    }

    public void DestroyTextureView(TextureViewHandle view)
    {
        if (!_textureViews.Remove(view.Id, out var v)) return;
        if (v.HasViewTexture) v.ViewTexture.Dispose();
    }

    // ---- Samplers ----

    public SamplerHandle CreateSampler(in SamplerDesc desc)
    {
        using var sd = new MTLSamplerDescriptor
        {
            MinFilter = MetalConvert.ToMetal(desc.MinFilter),
            MagFilter = MetalConvert.ToMetal(desc.MagFilter),
            MipFilter = MetalConvert.ToMetalMip(desc.Mipmap),
            SAddressMode = MetalConvert.ToMetal(desc.AddressU),
            TAddressMode = MetalConvert.ToMetal(desc.AddressV),
            RAddressMode = MetalConvert.ToMetal(desc.AddressW),
            LodMinClamp = desc.LodMinClamp,
            LodMaxClamp = desc.LodMaxClamp,
            MaxAnisotropy = (ulong)Math.Max(1f, desc.MaxAnisotropy),
            CompareFunction = desc.Compare.HasValue
                ? MetalConvert.ToMetal(desc.Compare.Value)
                : MTLCompareFunction.Never,
            BorderColor = MetalConvert.ToMetal(desc.Border),
            NormalizedCoordinates = true,
        };
        var samp = Device.NewSamplerState(sd);
        var id = NewHandleId();
        _samplers[id] = new MetalSampler { Sampler = samp };
        return new SamplerHandle(id);
    }

    public void DestroySampler(SamplerHandle sampler)
    {
        if (!_samplers.Remove(sampler.Id, out var s)) return;
        s.Sampler.Dispose();
    }

    public SamplerHandle GetSampler(in SamplerDesc desc)
    {
        var key = desc with { DebugName = null };
        if (_samplerCache.TryGetValue(key, out var h)) return h;
        h = CreateSampler(desc);
        _samplerCache[key] = h;
        return h;
    }

    // ---- Render targets ----

    public RenderTargetHandle CreateRenderTarget(in RenderTargetDesc desc)
    {
        var color = CreateTexture(new TextureDesc(
            desc.Width, desc.Height, desc.ColorFormat,
            TextureUsage.ColorAttachment | TextureUsage.Sampled | TextureUsage.CopySrc,
            SampleCount: desc.SampleCount, DebugName: desc.DebugName));
        var colorView = DefaultTextureView(color);
        var rt = new MetalRenderTarget
        {
            ColorTextureId = color.Id,
            ColorViewId = colorView.Id,
            Desc = desc,
        };
        if (desc.DepthStencilFormat.HasValue)
        {
            var depth = CreateTexture(new TextureDesc(
                desc.Width, desc.Height, desc.DepthStencilFormat.Value,
                TextureUsage.DepthStencilAttachment, SampleCount: desc.SampleCount, DebugName: desc.DebugName));
            var depthView = DefaultTextureView(depth);
            rt.DepthTextureId = depth.Id;
            rt.DepthViewId = depthView.Id;
        }
        var id = NewHandleId();
        _renderTargets[id] = rt;
        return new RenderTargetHandle(id);
    }

    public void DestroyRenderTarget(RenderTargetHandle rt)
    {
        if (!_renderTargets.Remove(rt.Id, out var r)) return;
        DestroyTexture(new TextureHandle(r.ColorTextureId));
        if (r.DepthTextureId != 0) DestroyTexture(new TextureHandle(r.DepthTextureId));
    }

    public TextureViewHandle GetRenderTargetColorView(RenderTargetHandle rt)
        => new(_renderTargets[rt.Id].ColorViewId);

    public TextureViewHandle GetRenderTargetDepthView(RenderTargetHandle rt)
        => new(_renderTargets[rt.Id].DepthViewId);

    // ---- Query pools ----

    public QueryPoolHandle CreateQueryPool(in QueryPoolDesc desc)
    {
        var bytes = (ulong)(desc.Count * sizeof(ulong));
        var backing = Device.NewBuffer(bytes, MTLResourceOptions.ResourceStorageModeShared);
        var id = NewHandleId();
        _queryPools[id] = new MetalQueryPool { Type = desc.Type, Count = desc.Count, Backing = backing };
        return new QueryPoolHandle(id);
    }

    public void DestroyQueryPool(QueryPoolHandle pool)
    {
        if (!_queryPools.Remove(pool.Id, out var p)) return;
        p.Backing.Dispose();
    }

    public void ReadQueryPool(QueryPoolHandle pool, int firstQuery, Span<ulong> results)
    {
        var p = _queryPools[pool.Id];
        var src = (ulong*)p.Backing.Contents.ToPointer();
        for (var i = 0; i < results.Length; i++)
            results[i] = src[firstQuery + i];
    }

    // ---- Fences ----

    public bool WaitForFence(FenceHandle fence, TimeSpan timeout)
    {
        if (!_fences.TryGetValue(fence.Id, out var f)) return true;
        // MTLEvent has no "wait" on the CPU side directly — the engine expects fences as
        // post-submit completion handles. We satisfy this by treating fences as already
        // signaled once their command buffer has committed; richer semantics need a
        // per-fence MTLSharedEvent + listener queue (not wired yet).
        return f.Signaled;
    }

    public bool IsFenceSignaled(FenceHandle fence)
        => !_fences.TryGetValue(fence.Id, out var f) || f.Signaled;

    public void DestroyFence(FenceHandle fence)
    {
        if (!_fences.Remove(fence.Id, out var f)) return;
        f.Event.Dispose();
    }
}
