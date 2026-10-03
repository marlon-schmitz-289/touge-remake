using System.Runtime.InteropServices;
using Silk.NET.OpenGL;

namespace Penelope.Backends.OpenGL;

public sealed unsafe partial class OpenGLDevice
{
    // ---- Buffers ----

    public BufferHandle CreateBuffer(in BufferDesc desc, ReadOnlySpan<byte> initialData = default)
    {
        var buf = new OpenGLBuffer
        {
            Buffer = Gl.GenBuffer(),
            SizeBytes = desc.SizeBytes,
            Usage = desc.Usage,
            Access = desc.Access,
        };

        var target = PickBufferTarget(desc.Usage);
        Gl.BindBuffer(target, buf.Buffer);

        // Use immutable storage with persistent+coherent mapping for host-visible buffers, so
        // streaming uploads (sprite batches) hit the same code path as Vulkan persistently-mapped
        // host-visible memory and avoid driver-side rename-buffer pessimization on glBufferData.
        if (desc.Access != BufferAccess.Immutable)
        {
            const MapBufferAccessMask flags = MapBufferAccessMask.WriteBit
                                              | MapBufferAccessMask.PersistentBit
                                              | MapBufferAccessMask.CoherentBit;
            const BufferStorageMask storageFlags = BufferStorageMask.MapWriteBit
                                                   | BufferStorageMask.MapPersistentBit
                                                   | BufferStorageMask.MapCoherentBit
                                                   | BufferStorageMask.DynamicStorageBit;
            // BufferStorage takes BufferStorageTarget (numerically identical to BufferTargetARB).
            fixed (byte* p = initialData)
            {
                Gl.BufferStorage((BufferStorageTarget)target, (nuint)desc.SizeBytes, p, storageFlags);
            }
            buf.MappedPtr = Gl.MapBufferRange(target, 0, (nuint)desc.SizeBytes, flags);
            buf.ImmutableStorage = true;
        }
        else
        {
            // Device-local immutable: glBufferData with StaticDraw.
            fixed (byte* p = initialData)
            {
                Gl.BufferData(target, (nuint)desc.SizeBytes, p, BufferUsageARB.StaticDraw);
            }
        }

        var id = NewHandleId();
        _buffers[id] = buf;
        return new BufferHandle(id);
    }

    public void WriteBuffer(BufferHandle buffer, int offsetBytes, ReadOnlySpan<byte> data)
    {
        var buf = _buffers[buffer.Id];
        if (buf.MappedPtr != null)
        {
            // Persistent+coherent mapping — write straight through.
            fixed (byte* src = data)
                System.Buffer.MemoryCopy(src, (byte*)buf.MappedPtr + offsetBytes, data.Length, data.Length);
            return;
        }
        // Immutable storage path: glBufferSubData (unsynchronized writes are OK; driver tracks).
        var target = PickBufferTarget(buf.Usage);
        Gl.BindBuffer(target, buf.Buffer);
        fixed (byte* src = data)
            Gl.BufferSubData(target, offsetBytes, (nuint)data.Length, src);
    }

    public Span<byte> MapBuffer(BufferHandle buffer, int offsetBytes, int sizeBytes)
    {
        var buf = _buffers[buffer.Id];
        if (buf.MappedPtr == null)
            throw new InvalidOperationException(
                "MapBuffer: buffer was created with BufferAccess.Immutable; only Dynamic/Stream buffers are persistently mapped.");
        return new Span<byte>((byte*)buf.MappedPtr + offsetBytes, sizeBytes);
    }

    public void UnmapBuffer(BufferHandle buffer)
    {
        // No-op: persistent mapping stays alive for the buffer's lifetime; matches Vulkan backend.
    }

    public void DestroyBuffer(BufferHandle buffer)
    {
        if (!_buffers.Remove(buffer.Id, out var b)) return;
        DeleteBuffer(b);
    }

    private static BufferTargetARB PickBufferTarget(BufferUsage usage)
    {
        // Pick the most natural target so binding doesn't pessimize subsequent draws. The actual
        // bind for use happens later (glBindVertexBuffer / glBindBufferRange / etc.).
        if ((usage & BufferUsage.Vertex) != 0) return BufferTargetARB.ArrayBuffer;
        if ((usage & BufferUsage.Index) != 0) return BufferTargetARB.ElementArrayBuffer;
        if ((usage & BufferUsage.Uniform) != 0) return BufferTargetARB.UniformBuffer;
        if ((usage & BufferUsage.Storage) != 0) return BufferTargetARB.ShaderStorageBuffer;
        if ((usage & BufferUsage.Indirect) != 0) return BufferTargetARB.DrawIndirectBuffer;
        return BufferTargetARB.CopyWriteBuffer;
    }

    // ---- Textures ----

    public TextureHandle CreateTexture(in TextureDesc desc, ReadOnlySpan<byte> initialData = default)
    {
        var (internalFmt, _, _, _) = OpenGLConvert.ToGl(desc.Format);
        var target = OpenGLConvert.ToGlTarget(desc.Dimension);

        uint texId = Gl.GenTexture();
        Gl.BindTexture(target, texId);
        // Allocate immutable storage — matches Vulkan's vkBindImageMemory + vkAllocateMemory.
        switch (desc.Dimension)
        {
            case TextureDimension.Tex2D:
                Gl.TexStorage2D(target, (uint)desc.MipLevels,
                    (SizedInternalFormat)internalFmt, (uint)desc.Width, (uint)desc.Height);
                break;
            case TextureDimension.Tex2DArray:
                Gl.TexStorage3D(target, (uint)desc.MipLevels,
                    (SizedInternalFormat)internalFmt, (uint)desc.Width, (uint)desc.Height, (uint)desc.ArrayLayers);
                break;
            case TextureDimension.Tex3D:
                Gl.TexStorage3D(target, (uint)desc.MipLevels,
                    (SizedInternalFormat)internalFmt, (uint)desc.Width, (uint)desc.Height, (uint)desc.Depth);
                break;
            case TextureDimension.Cube:
                Gl.TexStorage2D(target, (uint)desc.MipLevels,
                    (SizedInternalFormat)internalFmt, (uint)desc.Width, (uint)desc.Height);
                break;
            case TextureDimension.CubeArray:
                Gl.TexStorage3D(target, (uint)desc.MipLevels,
                    (SizedInternalFormat)internalFmt, (uint)desc.Width, (uint)desc.Height, (uint)(desc.ArrayLayers * 6));
                break;
        }

        // Default sane filter — overridden by SamplerHandle bindings at draw time. These params
        // matter only for completeness: a bound texture without a sampler must still be complete.
        Gl.TexParameter(target, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        Gl.TexParameter(target, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        Gl.TexParameter(target, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        Gl.TexParameter(target, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        Gl.TexParameter(target, TextureParameterName.TextureBaseLevel, 0);
        Gl.TexParameter(target, TextureParameterName.TextureMaxLevel, desc.MipLevels - 1);

        var tex = new OpenGLTexture
        {
            Texture = texId,
            Target = target,
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
        _textures[id] = tex;

        // Default view (alias) covering all mips/layers.
        var defView = new OpenGLTextureView
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
        tex.DefaultViewId = defViewId;

        if (!initialData.IsEmpty)
        {
            WriteTexture(new TextureHandle(id),
                mipLevel: 0, arrayLayer: 0,
                x: 0, y: 0, z: 0,
                width: desc.Width, height: desc.Height, depth: desc.Depth,
                data: initialData,
                bytesPerRow: desc.Width * OpenGLConvert.BytesPerPixel(desc.Format),
                rowsPerImage: desc.Height);
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
        var (internalFmt, fmt, type, bpp) = OpenGLConvert.ToGl(tex.Format);

        // No row flip on upload. With per-pass UPPER_LEFT clip control for offscreen RTs, the
        // sampling/storage convention now matches Vulkan: texel (0,0) is the first stored row.
        // CPU uploads write `bytesPerRow * y` straight from source data, so source row 0 lands at
        // texel (0,0) and UV (0,0) reads source row 0 = top of image — same as Vulkan.
        Gl.PixelStore(PixelStoreParameter.UnpackRowLength, bytesPerRow / bpp);
        Gl.BindTexture(tex.Target, tex.Texture);

        fixed (byte* p = data)
        {
            switch (tex.Dimension)
            {
                case TextureDimension.Tex2D:
                    Gl.TexSubImage2D(tex.Target, mipLevel, x, y,
                        (uint)width, (uint)height, fmt, type, p);
                    break;
                case TextureDimension.Tex2DArray:
                    Gl.TexSubImage3D(tex.Target, mipLevel, x, y, arrayLayer,
                        (uint)width, (uint)height, (uint)depth, fmt, type, p);
                    break;
                case TextureDimension.Tex3D:
                    Gl.TexSubImage3D(tex.Target, mipLevel, x, y, z,
                        (uint)width, (uint)height, (uint)depth, fmt, type, p);
                    break;
                case TextureDimension.Cube:
                    var face = TextureTarget.TextureCubeMapPositiveX + arrayLayer;
                    Gl.TexSubImage2D(face, mipLevel, x, y,
                        (uint)width, (uint)height, fmt, type, p);
                    break;
                case TextureDimension.CubeArray:
                    Gl.TexSubImage3D(tex.Target, mipLevel, x, y, arrayLayer,
                        (uint)width, (uint)height, (uint)depth, fmt, type, p);
                    break;
            }
        }
        Gl.PixelStore(PixelStoreParameter.UnpackRowLength, 0);
    }

    public void DestroyTexture(TextureHandle texture)
    {
        if (!_textures.Remove(texture.Id, out var t)) return;
        if (t.DefaultViewId != 0 && _textureViews.Remove(t.DefaultViewId, out var defView))
            if (defView.ViewTexture != 0) Gl.DeleteTexture(defView.ViewTexture);
        DeleteTexture(t);
    }

    public TextureViewHandle DefaultTextureView(TextureHandle texture)
    {
        var tex = _textures[texture.Id];
        return new TextureViewHandle(tex.DefaultViewId);
    }

    public TextureViewHandle CreateTextureView(TextureHandle texture, in TextureViewDesc desc)
    {
        var tex = _textures[texture.Id];
        var fmt = desc.Format ?? tex.Format;
        var mipCount = desc.MipLevelCount == 0 ? tex.MipLevels - desc.BaseMipLevel : desc.MipLevelCount;
        var layerCount = desc.ArrayLayerCount == 0 ? tex.ArrayLayers - desc.BaseArrayLayer : desc.ArrayLayerCount;

        var view = new OpenGLTextureView
        {
            TextureId = texture.Id,
            Format = fmt,
            Aspect = desc.Aspect,
            BaseMip = desc.BaseMipLevel,
            MipCount = mipCount,
            BaseLayer = desc.BaseArrayLayer,
            LayerCount = layerCount,
        };

        // If the view diverges from the default (subset of mips/layers OR reformat), realize it
        // as an actual GL texture view. Otherwise the view is just metadata aliasing the texture.
        if (desc.BaseMipLevel != 0 || mipCount != tex.MipLevels ||
            desc.BaseArrayLayer != 0 || layerCount != tex.ArrayLayers ||
            (desc.Format.HasValue && desc.Format.Value != tex.Format))
        {
            var (internalFmt, _, _, _) = OpenGLConvert.ToGl(fmt);
            uint vtex = Gl.GenTexture();
            var viewTarget = OpenGLConvert.ToGlTarget(desc.Dimension ?? tex.Dimension);
            Gl.TextureView(vtex, viewTarget, tex.Texture, (SizedInternalFormat)internalFmt,
                (uint)desc.BaseMipLevel, (uint)mipCount, (uint)desc.BaseArrayLayer, (uint)layerCount);
            view.ViewTexture = vtex;
        }

        var id = NewHandleId();
        _textureViews[id] = view;
        return new TextureViewHandle(id);
    }

    public void DestroyTextureView(TextureViewHandle view)
    {
        if (!_textureViews.Remove(view.Id, out var v)) return;
        if (v.ViewTexture != 0) Gl.DeleteTexture(v.ViewTexture);
    }

    /// <summary>Returns the GL texture id behind a Penelope view (real texture or aliased view).</summary>
    internal uint GetViewGlTexture(OpenGLTextureView view)
    {
        if (view.ViewTexture != 0) return view.ViewTexture;
        var t = _textures[view.TextureId];
        return t.Texture;
    }

    // ---- Samplers ----

    public SamplerHandle CreateSampler(in SamplerDesc desc)
    {
        var samp = Gl.GenSampler();
        Gl.SamplerParameter(samp, SamplerParameterI.MinFilter,
            (int)OpenGLConvert.ToGlMin(desc.MinFilter, desc.Mipmap));
        Gl.SamplerParameter(samp, SamplerParameterI.MagFilter,
            (int)OpenGLConvert.ToGlMag(desc.MagFilter));
        Gl.SamplerParameter(samp, SamplerParameterI.WrapS, (int)OpenGLConvert.ToGlWrap(desc.AddressU));
        Gl.SamplerParameter(samp, SamplerParameterI.WrapT, (int)OpenGLConvert.ToGlWrap(desc.AddressV));
        Gl.SamplerParameter(samp, SamplerParameterI.WrapR, (int)OpenGLConvert.ToGlWrap(desc.AddressW));
        Gl.SamplerParameter(samp, SamplerParameterF.MinLod, desc.LodMinClamp);
        Gl.SamplerParameter(samp, SamplerParameterF.MaxLod, desc.LodMaxClamp);
        if (desc.MaxAnisotropy > 1f)
        {
            // Core in 4.6, ARB_texture_filter_anisotropic in 4.5.
            Gl.SamplerParameter(samp, (SamplerParameterF)0x84FE /* TEXTURE_MAX_ANISOTROPY */, desc.MaxAnisotropy);
        }
        if (desc.Compare.HasValue)
        {
            Gl.SamplerParameter(samp, SamplerParameterI.CompareMode, (int)TextureCompareMode.CompareRefToTexture);
            Gl.SamplerParameter(samp, SamplerParameterI.CompareFunc, (int)OpenGLConvert.ToGlDepth(desc.Compare.Value));
        }
        else
        {
            Gl.SamplerParameter(samp, SamplerParameterI.CompareMode, (int)TextureCompareMode.None);
        }

        // Border color
        Span<float> border = desc.Border switch
        {
            BorderColor.OpaqueBlack => stackalloc float[] { 0, 0, 0, 1 },
            BorderColor.OpaqueWhite => stackalloc float[] { 1, 1, 1, 1 },
            _ => stackalloc float[] { 0, 0, 0, 0 },
        };
        fixed (float* pb = border)
            Gl.SamplerParameter(samp, SamplerParameterF.BorderColor, pb);

        var id = NewHandleId();
        _samplers[id] = new OpenGLSampler { Sampler = samp };
        return new SamplerHandle(id);
    }

    public void DestroySampler(SamplerHandle sampler)
    {
        if (!_samplers.Remove(sampler.Id, out var s)) return;
        Gl.DeleteSampler(s.Sampler);
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
        var rt = new OpenGLRenderTarget
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

    public TextureViewHandle GetRenderTargetColorView(RenderTargetHandle rt) =>
        new(_renderTargets[rt.Id].ColorViewId);

    public TextureViewHandle GetRenderTargetDepthView(RenderTargetHandle rt) =>
        new(_renderTargets[rt.Id].DepthViewId);

    // ---- Query pools ----

    public QueryPoolHandle CreateQueryPool(in QueryPoolDesc desc)
    {
        var qs = new uint[desc.Count];
        for (var i = 0; i < desc.Count; i++) qs[i] = Gl.GenQuery();
        var id = NewHandleId();
        _queryPools[id] = new OpenGLQueryPool { Queries = qs, Type = desc.Type };
        return new QueryPoolHandle(id);
    }

    public void DestroyQueryPool(QueryPoolHandle pool)
    {
        if (!_queryPools.Remove(pool.Id, out var p)) return;
        foreach (var q in p.Queries) Gl.DeleteQuery(q);
    }

    public void ReadQueryPool(QueryPoolHandle pool, int firstQuery, Span<ulong> results)
    {
        var p = _queryPools[pool.Id];
        for (var i = 0; i < results.Length; i++)
        {
            ulong v = 0;
            Gl.GetQueryObject(p.Queries[firstQuery + i], QueryObjectParameterName.Result, &v);
            results[i] = v;
        }
    }

    // ---- Fences ----

    public bool WaitForFence(FenceHandle fence, TimeSpan timeout)
    {
        if (!_fences.TryGetValue(fence.Id, out var f)) return true;
        if (f.Sync == 0) return true;
        var nanos = (ulong)Math.Max(0, timeout.TotalMilliseconds * 1_000_000);
        var r = Gl.ClientWaitSync(f.Sync, SyncObjectMask.Bit, nanos);
        return r == GLEnum.AlreadySignaled || r == GLEnum.ConditionSatisfied;
    }

    public bool IsFenceSignaled(FenceHandle fence)
    {
        if (!_fences.TryGetValue(fence.Id, out var f)) return true;
        if (f.Sync == 0) return true;
        var r = Gl.ClientWaitSync(f.Sync, (SyncObjectMask)0, 0);
        return r == GLEnum.AlreadySignaled || r == GLEnum.ConditionSatisfied;
    }

    public void DestroyFence(FenceHandle fence)
    {
        if (!_fences.Remove(fence.Id, out var f)) return;
        if (f.Sync != 0) Gl.DeleteSync(f.Sync);
    }
}
