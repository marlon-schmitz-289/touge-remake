using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace Penelope.Backends.Vulkan;

public sealed unsafe partial class VulkanDevice
{
    // ---- Buffers ----

    public BufferHandle CreateBuffer(in BufferDesc desc, ReadOnlySpan<byte> initialData = default)
    {
        var usage = VulkanConvert.ToVk(desc.Usage) | BufferUsageFlags.TransferDstBit | BufferUsageFlags.TransferSrcBit;

        var info = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = (ulong)desc.SizeBytes,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
        };
        Vk.CreateBuffer(Device, &info, null, out var buf).ThrowIfError();

        Vk.GetBufferMemoryRequirements(Device, buf, out var req);

        var props = desc.Access == BufferAccess.Immutable
            ? MemoryPropertyFlags.DeviceLocalBit
            : MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;

        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = req.Size,
            MemoryTypeIndex = FindMemoryType(req.MemoryTypeBits, props),
        };
        Vk.AllocateMemory(Device, &allocInfo, null, out var mem).ThrowIfError();
        Vk.BindBufferMemory(Device, buf, mem, 0);

        var wrapper = new VulkanBuffer
        {
            Buffer = buf,
            Memory = mem,
            Size = (ulong)desc.SizeBytes,
            Usage = desc.Usage,
            Access = desc.Access,
            HostVisible = desc.Access != BufferAccess.Immutable,
        };

        if (wrapper.HostVisible)
        {
            void* p = null;
            Vk.MapMemory(Device, mem, 0, (ulong)desc.SizeBytes, 0, ref p);
            wrapper.MappedPtr = p;
        }

        var id = NewHandleId();
        _buffers[id] = wrapper;

        if (!initialData.IsEmpty)
            WriteBuffer(new BufferHandle(id), 0, initialData);

        return new BufferHandle(id);
    }

    public void WriteBuffer(BufferHandle buffer, int offsetBytes, ReadOnlySpan<byte> data)
    {
        if (!_buffers.TryGetValue(buffer.Id, out var buf)) throw new ArgumentException("Unknown buffer.");
        if (!buf.HostVisible)
        {
            // Staging copy
            using var staging = CreateStaging(data.Length, data);
            CopyBufferImmediate(staging.Buffer, buf.Buffer, (ulong)data.Length, 0, (ulong)offsetBytes);
            return;
        }

        void* dstPtr;
        if (buf.MappedPtr != null)
        {
            dstPtr = (byte*)buf.MappedPtr + offsetBytes;
        }
        else
        {
            void* p = null;
            Vk.MapMemory(Device, buf.Memory, (ulong)offsetBytes, (ulong)data.Length, 0, ref p).ThrowIfError();
            dstPtr = p;
        }

        fixed (byte* src = data)
            System.Buffer.MemoryCopy(src, dstPtr, data.Length, data.Length);

        if (buf.MappedPtr == null)
            Vk.UnmapMemory(Device, buf.Memory);
    }

    public Span<byte> MapBuffer(BufferHandle buffer, int offsetBytes, int sizeBytes)
    {
        if (!_buffers.TryGetValue(buffer.Id, out var buf)) throw new ArgumentException("Unknown buffer.");
        if (buf.MappedPtr == null)
        {
            void* p = null;
            Vk.MapMemory(Device, buf.Memory, 0, buf.Size, 0, ref p).ThrowIfError();
            buf.MappedPtr = p;
        }
        return new Span<byte>((byte*)buf.MappedPtr + offsetBytes, sizeBytes);
    }

    public void UnmapBuffer(BufferHandle buffer)
    {
        // No-op: HostVisible buffers are persistently mapped at creation; unmapping
        // mid-life would break SpriteBatch / LightRenderer streaming writes that
        // depend on the persistent pointer staying valid. The buffer is unmapped
        // exactly once, in DestroyBufferInternal.
    }

    public void DestroyBuffer(BufferHandle buffer)
    {
        if (!_buffers.Remove(buffer.Id, out var buf)) return;
        DestroyBufferInternal(buf);
    }

    internal void DestroyBufferInternal(VulkanBuffer buf)
    {
        if (buf.MappedPtr != null) Vk.UnmapMemory(Device, buf.Memory);
        Vk.DestroyBuffer(Device, buf.Buffer, null);
        Vk.FreeMemory(Device, buf.Memory, null);
    }

    // ---- Textures ----

    public TextureHandle CreateTexture(in TextureDesc desc, ReadOnlySpan<byte> initialData = default)
    {
        var fmt = VulkanConvert.ToVk(desc.Format);

        var info = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = VulkanConvert.ToVkImageType(desc.Dimension),
            Format = fmt,
            Extent = new Extent3D((uint)desc.Width, (uint)desc.Height, (uint)desc.Depth),
            MipLevels = (uint)desc.MipLevels,
            ArrayLayers = (uint)desc.ArrayLayers,
            Samples = VulkanConvert.ToVk(desc.SampleCount),
            Tiling = ImageTiling.Optimal,
            Usage = VulkanConvert.ToVk(desc.Usage) | ImageUsageFlags.TransferDstBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
            Flags = desc.Dimension is TextureDimension.Cube or TextureDimension.CubeArray
                ? ImageCreateFlags.CreateCubeCompatibleBit
                : 0,
        };
        Vk.CreateImage(Device, &info, null, out var img).ThrowIfError();

        Vk.GetImageMemoryRequirements(Device, img, out var req);
        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = req.Size,
            MemoryTypeIndex = FindMemoryType(req.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
        };
        Vk.AllocateMemory(Device, &allocInfo, null, out var mem).ThrowIfError();
        Vk.BindImageMemory(Device, img, mem, 0);

        var wrapper = new VulkanImage
        {
            Image = img,
            Memory = mem,
            Format = fmt,
            Extent = info.Extent,
            MipLevels = info.MipLevels,
            ArrayLayers = info.ArrayLayers,
            Samples = info.Samples,
            Usage = desc.Usage,
            Dimension = desc.Dimension,
            CurrentLayout = ImageLayout.Undefined,
        };

        var id = RegisterImage(wrapper);

        // Default view
        var viewAspect = (desc.Usage & TextureUsage.DepthStencilAttachment) != 0
            ? VulkanConvert.ToVk(TextureAspect.All, fmt)
            : ImageAspectFlags.ColorBit;
        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = img,
            ViewType = VulkanConvert.ToVkViewType(desc.Dimension),
            Format = fmt,
            Components = new ComponentMapping(
                ComponentSwizzle.Identity, ComponentSwizzle.Identity,
                ComponentSwizzle.Identity, ComponentSwizzle.Identity),
            SubresourceRange = new ImageSubresourceRange(viewAspect, 0, info.MipLevels, 0, info.ArrayLayers),
        };
        Vk.CreateImageView(Device, &viewInfo, null, out var view).ThrowIfError();
        var viewWrapper = new VulkanImageView
        {
            View = view,
            ImageId = id,
            Format = fmt,
            Aspect = viewAspect,
            BaseMip = 0,
            MipCount = info.MipLevels,
            BaseLayer = 0,
            LayerCount = info.ArrayLayers,
        };
        wrapper.DefaultViewId = RegisterImageView(viewWrapper);

        if (!initialData.IsEmpty)
        {
            WriteTexture(new TextureHandle(id), 0, 0, 0, 0, 0, desc.Width, desc.Height, desc.Depth, initialData,
                bytesPerRow: desc.Width * BytesPerPixel(desc.Format),
                rowsPerImage: desc.Height);
        }

        return new TextureHandle(id);
    }

    public void WriteTexture(
        TextureHandle texture,
        int mipLevel,
        int arrayLayer,
        int x, int y, int z,
        int width, int height, int depth,
        ReadOnlySpan<byte> data,
        int bytesPerRow,
        int rowsPerImage)
    {
        if (!_images.TryGetValue(texture.Id, out var img)) throw new ArgumentException("Unknown texture.");

        using var staging = CreateStaging(data.Length, data);

        // Transition dst to TransferDstOptimal, copy, transition to ShaderReadOnlyOptimal.
        ExecuteImmediate(cb =>
        {
            TransitionImageLayout(cb, img.Image, img.Format,
                ImageLayout.Undefined, ImageLayout.TransferDstOptimal,
                (uint)mipLevel, 1, (uint)arrayLayer, 1);

            var copy = new BufferImageCopy
            {
                BufferOffset = 0,
                BufferRowLength = 0,
                BufferImageHeight = 0,
                ImageSubresource = new ImageSubresourceLayers(
                    ImageAspectFlags.ColorBit, (uint)mipLevel, (uint)arrayLayer, 1),
                ImageOffset = new Offset3D(x, y, z),
                ImageExtent = new Extent3D((uint)width, (uint)height, (uint)depth),
            };
            Vk.CmdCopyBufferToImage(cb, staging.Buffer, img.Image,
                ImageLayout.TransferDstOptimal, 1, &copy);

            var targetLayout = (img.Usage & TextureUsage.Sampled) != 0
                ? ImageLayout.ShaderReadOnlyOptimal
                : ImageLayout.General;
            TransitionImageLayout(cb, img.Image, img.Format,
                ImageLayout.TransferDstOptimal, targetLayout,
                (uint)mipLevel, 1, (uint)arrayLayer, 1);
            img.CurrentLayout = targetLayout;
        });
    }

    public void DestroyTexture(TextureHandle texture)
    {
        if (!_images.Remove(texture.Id, out var img)) return;
        if (img.DefaultViewId != 0 && _imageViews.Remove(img.DefaultViewId, out var defView))
            Vk.DestroyImageView(Device, defView.View, null);
        DestroyImageInternal(img);
    }

    internal void DestroyImageInternal(VulkanImage img)
    {
        if (!img.IsSwapchainImage)
        {
            Vk.DestroyImage(Device, img.Image, null);
            Vk.FreeMemory(Device, img.Memory, null);
        }
    }

    public TextureViewHandle DefaultTextureView(TextureHandle texture)
    {
        if (!_images.TryGetValue(texture.Id, out var img)) throw new ArgumentException("Unknown texture.");
        return new TextureViewHandle(img.DefaultViewId);
    }

    public TextureViewHandle CreateTextureView(TextureHandle texture, in TextureViewDesc desc)
    {
        if (!_images.TryGetValue(texture.Id, out var img)) throw new ArgumentException("Unknown texture.");
        var fmt = desc.Format.HasValue ? VulkanConvert.ToVk(desc.Format.Value) : img.Format;
        var aspect = VulkanConvert.ToVk(desc.Aspect, fmt);
        var mipCount = desc.MipLevelCount == 0 ? img.MipLevels - (uint)desc.BaseMipLevel : (uint)desc.MipLevelCount;
        var layerCount = desc.ArrayLayerCount == 0 ? img.ArrayLayers - (uint)desc.BaseArrayLayer : (uint)desc.ArrayLayerCount;

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = img.Image,
            ViewType = VulkanConvert.ToVkViewType(desc.Dimension ?? img.Dimension),
            Format = fmt,
            Components = new ComponentMapping(
                ComponentSwizzle.Identity, ComponentSwizzle.Identity,
                ComponentSwizzle.Identity, ComponentSwizzle.Identity),
            SubresourceRange = new ImageSubresourceRange(aspect, (uint)desc.BaseMipLevel, mipCount, (uint)desc.BaseArrayLayer, layerCount),
        };
        Vk.CreateImageView(Device, &viewInfo, null, out var view).ThrowIfError();
        var wrapper = new VulkanImageView
        {
            View = view,
            ImageId = texture.Id,
            Format = fmt,
            Aspect = aspect,
            BaseMip = (uint)desc.BaseMipLevel,
            MipCount = mipCount,
            BaseLayer = (uint)desc.BaseArrayLayer,
            LayerCount = layerCount,
        };
        return new TextureViewHandle(RegisterImageView(wrapper));
    }

    public void DestroyTextureView(TextureViewHandle view)
    {
        if (!_imageViews.Remove(view.Id, out var v)) return;
        Vk.DestroyImageView(Device, v.View, null);
    }

    // ---- Samplers ----

    public SamplerHandle CreateSampler(in SamplerDesc desc)
    {
        var info = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MinFilter = VulkanConvert.ToVk(desc.MinFilter),
            MagFilter = VulkanConvert.ToVk(desc.MagFilter),
            MipmapMode = VulkanConvert.ToVk(desc.Mipmap),
            AddressModeU = VulkanConvert.ToVk(desc.AddressU),
            AddressModeV = VulkanConvert.ToVk(desc.AddressV),
            AddressModeW = VulkanConvert.ToVk(desc.AddressW),
            MinLod = desc.LodMinClamp,
            MaxLod = desc.LodMaxClamp,
            AnisotropyEnable = desc.MaxAnisotropy > 1f,
            MaxAnisotropy = desc.MaxAnisotropy,
            CompareEnable = desc.Compare.HasValue,
            CompareOp = desc.Compare.HasValue ? VulkanConvert.ToVk(desc.Compare.Value) : CompareOp.Never,
            BorderColor = VulkanConvert.ToVk(desc.Border),
            UnnormalizedCoordinates = false,
        };
        Vk.CreateSampler(Device, &info, null, out var sampler).ThrowIfError();

        var id = NewHandleId();
        _samplers[id] = new VulkanSampler { Sampler = sampler };
        return new SamplerHandle(id);
    }

    public void DestroySampler(SamplerHandle sampler)
    {
        if (!_samplers.Remove(sampler.Id, out var s)) return;
        Vk.DestroySampler(Device, s.Sampler, null);
    }

    public SamplerHandle GetSampler(in SamplerDesc desc)
    {
        // Normalise DebugName out of the cache key — two callers asking for SamplerDesc.Linear
        // with different debug labels still share the underlying sampler.
        var key = desc with { DebugName = null };
        if (_samplerCache.TryGetValue(key, out var h)) return h;
        h = CreateSampler(desc);
        _samplerCache[key] = h;
        return h;
    }

    // ---- Shaders ----

    public ShaderHandle CreateShader(ShaderSource source)
    {
        var sh = new VulkanShader
        {
            VertexEntry = source.VertexEntry,
            FragmentEntry = source.FragmentEntry,
            ComputeEntry = source.ComputeEntry,
        };

        if (source.VertexSpirv != null)
        {
            sh.VertexModule = CreateModule(source.VertexSpirv);
            sh.HasVertex = true;
        }
        else if (source.VertexGlsl != null)
        {
            throw new NotSupportedException(
                "Vulkan backend requires pre-compiled SPIR-V. Provide VertexSpirv, or integrate shaderc/glslang for runtime compilation.");
        }

        if (source.FragmentSpirv != null)
        {
            sh.FragmentModule = CreateModule(source.FragmentSpirv);
            sh.HasFragment = true;
        }
        else if (source.FragmentGlsl != null)
        {
            throw new NotSupportedException("Vulkan backend requires pre-compiled SPIR-V for fragment.");
        }

        if (source.ComputeSpirv != null)
        {
            sh.ComputeModule = CreateModule(source.ComputeSpirv);
            sh.HasCompute = true;
        }
        else if (source.ComputeGlsl != null)
        {
            throw new NotSupportedException("Vulkan backend requires pre-compiled SPIR-V for compute.");
        }

        var id = NewHandleId();
        _shaders[id] = sh;
        return new ShaderHandle(id);
    }

    private ShaderModule CreateModule(byte[] spirv)
    {
        fixed (byte* p = spirv)
        {
            var info = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)spirv.Length,
                PCode = (uint*)p,
            };
            Vk.CreateShaderModule(Device, &info, null, out var module).ThrowIfError();
            return module;
        }
    }

    public void DestroyShader(ShaderHandle shader)
    {
        if (!_shaders.Remove(shader.Id, out var sh)) return;
        DestroyShaderInternal(sh);
    }

    internal void DestroyShaderInternal(VulkanShader sh)
    {
        if (sh.HasVertex) Vk.DestroyShaderModule(Device, sh.VertexModule, null);
        if (sh.HasFragment) Vk.DestroyShaderModule(Device, sh.FragmentModule, null);
        if (sh.HasCompute) Vk.DestroyShaderModule(Device, sh.ComputeModule, null);
    }

    // ---- Bind group layouts & bind groups ----

    public BindGroupLayoutHandle CreateBindGroupLayout(in BindGroupLayoutDesc desc)
    {
        var bindings = new DescriptorSetLayoutBinding[desc.Entries.Length];
        for (var i = 0; i < desc.Entries.Length; i++)
        {
            var e = desc.Entries[i];
            bindings[i] = new DescriptorSetLayoutBinding
            {
                Binding = (uint)e.Binding,
                DescriptorCount = 1,
                DescriptorType = e.HasDynamicOffset && e.Type == BindingType.UniformBuffer
                    ? DescriptorType.UniformBufferDynamic
                    : VulkanConvert.ToVk(e.Type),
                StageFlags = VulkanConvert.ToVk(e.Visibility),
            };
        }

        fixed (DescriptorSetLayoutBinding* pB = bindings)
        {
            var info = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = (uint)bindings.Length,
                PBindings = pB,
            };
            Vk.CreateDescriptorSetLayout(Device, &info, null, out var layout).ThrowIfError();

            var id = NewHandleId();
            _bindGroupLayouts[id] = new VulkanBindGroupLayout { Layout = layout, Entries = desc.Entries };
            return new BindGroupLayoutHandle(id);
        }
    }

    public void DestroyBindGroupLayout(BindGroupLayoutHandle layout)
    {
        if (!_bindGroupLayouts.Remove(layout.Id, out var l)) return;
        Vk.DestroyDescriptorSetLayout(Device, l.Layout, null);
    }

    public BindGroupLayoutHandle GetBindGroupLayout(in BindGroupLayoutDesc desc)
    {
        var key = new BindGroupLayoutKey(desc);
        if (_bgLayoutCache.TryGetValue(key, out var h)) return h;
        h = CreateBindGroupLayout(desc);
        _bgLayoutCache[key] = h;
        return h;
    }

    public BindGroupHandle CreateBindGroup(in BindGroupDesc desc)
    {
        if (!_bindGroupLayouts.TryGetValue(desc.Layout.Id, out var layout))
            throw new ArgumentException("Unknown bind group layout.");

        var layoutHandle = layout.Layout;
        var allocInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = DescriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = &layoutHandle,
        };
        DescriptorSet set;
        Vk.AllocateDescriptorSets(Device, &allocInfo, &set).ThrowIfError();

        // Write all bindings
        var writes = new WriteDescriptorSet[desc.Entries.Length];
        var bufInfos = new DescriptorBufferInfo[desc.Entries.Length];
        var imgInfos = new DescriptorImageInfo[desc.Entries.Length];

        for (var i = 0; i < desc.Entries.Length; i++)
        {
            var e = desc.Entries[i];
            var entryLayout = FindLayoutEntry(layout, e.Binding);
            var type = entryLayout.HasDynamicOffset && entryLayout.Type == BindingType.UniformBuffer
                ? DescriptorType.UniformBufferDynamic
                : VulkanConvert.ToVk(entryLayout.Type);

            writes[i] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = set,
                DstBinding = (uint)e.Binding,
                DescriptorCount = 1,
                DescriptorType = type,
            };

            if (!e.Buffer.IsNull)
            {
                var buf = _buffers[e.Buffer.Id];
                bufInfos[i] = new DescriptorBufferInfo
                {
                    Buffer = buf.Buffer,
                    Offset = (ulong)e.BufferOffset,
                    Range = e.BufferSize > 0 ? (ulong)e.BufferSize : buf.Size - (ulong)e.BufferOffset,
                };
            }
            else if (!e.TextureView.IsNull && !e.Sampler.IsNull)
            {
                // Combined image sampler.
                var view = _imageViews[e.TextureView.Id];
                var samp = _samplers[e.Sampler.Id];
                imgInfos[i] = new DescriptorImageInfo
                {
                    ImageView = view.View,
                    ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
                    Sampler = samp.Sampler,
                };
            }
            else if (!e.TextureView.IsNull)
            {
                var view = _imageViews[e.TextureView.Id];
                imgInfos[i] = new DescriptorImageInfo
                {
                    ImageView = view.View,
                    ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
                };
            }
            else if (!e.Sampler.IsNull)
            {
                var samp = _samplers[e.Sampler.Id];
                imgInfos[i] = new DescriptorImageInfo { Sampler = samp.Sampler };
            }
        }

        fixed (WriteDescriptorSet* pW = writes)
        fixed (DescriptorBufferInfo* pB = bufInfos)
        fixed (DescriptorImageInfo* pI = imgInfos)
        {
            for (var i = 0; i < writes.Length; i++)
            {
                if (!desc.Entries[i].Buffer.IsNull) pW[i].PBufferInfo = &pB[i];
                else pW[i].PImageInfo = &pI[i];
            }
            Vk.UpdateDescriptorSets(Device, (uint)writes.Length, pW, 0, null);
        }

        var id = NewHandleId();
        _bindGroups[id] = new VulkanBindGroup { Set = set, LayoutId = desc.Layout.Id };
        return new BindGroupHandle(id);
    }

    private static BindGroupLayoutEntry FindLayoutEntry(VulkanBindGroupLayout layout, int binding)
    {
        foreach (var e in layout.Entries) if (e.Binding == binding) return e;
        throw new ArgumentException($"Binding {binding} not found in layout.");
    }

    public void DestroyBindGroup(BindGroupHandle group)
    {
        if (!_bindGroups.Remove(group.Id, out var g)) return;
        var set = g.Set;
        Vk.FreeDescriptorSets(Device, DescriptorPool, 1, &set);
    }

    // ---- Render targets ----

    public RenderTargetHandle CreateRenderTarget(in RenderTargetDesc desc)
    {
        var color = CreateTexture(new TextureDesc(
            desc.Width, desc.Height, desc.ColorFormat,
            TextureUsage.ColorAttachment | TextureUsage.Sampled | TextureUsage.CopySrc,
            SampleCount: desc.SampleCount,
            DebugName: desc.DebugName));
        var colorView = DefaultTextureView(color);

        var rt = new VulkanRenderTarget
        {
            ColorImageId = color.Id,
            ColorViewId = colorView.Id,
            Desc = desc,
        };

        if (desc.DepthStencilFormat.HasValue)
        {
            var depth = CreateTexture(new TextureDesc(
                desc.Width, desc.Height, desc.DepthStencilFormat.Value,
                TextureUsage.DepthStencilAttachment,
                SampleCount: desc.SampleCount,
                DebugName: desc.DebugName));
            var depthView = DefaultTextureView(depth);
            rt.DepthImageId = depth.Id;
            rt.DepthViewId = depthView.Id;
        }

        var id = NewHandleId();
        _renderTargets[id] = rt;
        return new RenderTargetHandle(id);
    }

    public void DestroyRenderTarget(RenderTargetHandle rt)
    {
        if (!_renderTargets.Remove(rt.Id, out var r)) return;
        DestroyTexture(new TextureHandle(r.ColorImageId));
        if (r.DepthImageId != 0) DestroyTexture(new TextureHandle(r.DepthImageId));
    }

    public TextureViewHandle GetRenderTargetColorView(RenderTargetHandle rt) =>
        new(_renderTargets[rt.Id].ColorViewId);

    public TextureViewHandle GetRenderTargetDepthView(RenderTargetHandle rt) =>
        new(_renderTargets[rt.Id].DepthViewId);

    // ---- Query pools ----

    public QueryPoolHandle CreateQueryPool(in QueryPoolDesc desc)
    {
        var info = new QueryPoolCreateInfo
        {
            SType = StructureType.QueryPoolCreateInfo,
            QueryType = desc.Type switch
            {
                QueryType.Timestamp => Silk.NET.Vulkan.QueryType.Timestamp,
                QueryType.Occlusion => Silk.NET.Vulkan.QueryType.Occlusion,
                QueryType.PipelineStatistics => Silk.NET.Vulkan.QueryType.PipelineStatistics,
                _ => Silk.NET.Vulkan.QueryType.Timestamp,
            },
            QueryCount = (uint)desc.Count,
        };
        Vk.CreateQueryPool(Device, &info, null, out var pool).ThrowIfError();

        var id = NewHandleId();
        _queryPools[id] = new VulkanQueryPool { Pool = pool, Type = desc.Type, Count = desc.Count };
        return new QueryPoolHandle(id);
    }

    public void DestroyQueryPool(QueryPoolHandle pool)
    {
        if (!_queryPools.Remove(pool.Id, out var p)) return;
        Vk.DestroyQueryPool(Device, p.Pool, null);
    }

    public void ReadQueryPool(QueryPoolHandle pool, int firstQuery, Span<ulong> results)
    {
        if (!_queryPools.TryGetValue(pool.Id, out var p)) throw new ArgumentException("Unknown query pool.");
        fixed (ulong* pResults = results)
        {
            Vk.GetQueryPoolResults(
                Device, p.Pool,
                (uint)firstQuery, (uint)results.Length,
                (nuint)(results.Length * sizeof(ulong)), pResults,
                sizeof(ulong), QueryResultFlags.Result64Bit | QueryResultFlags.ResultWaitBit).ThrowIfError();
        }
    }

    // ---- Fences ----

    public bool WaitForFence(FenceHandle fence, TimeSpan timeout)
    {
        if (!_fences.TryGetValue(fence.Id, out var f)) return true;
        var tNs = (ulong)Math.Max(0, timeout.TotalMilliseconds * 1_000_000);
        var vkFence = f.Fence;
        var result = Vk.WaitForFences(Device, 1, &vkFence, true, tNs);
        return result == Result.Success;
    }

    public bool IsFenceSignaled(FenceHandle fence)
    {
        if (!_fences.TryGetValue(fence.Id, out var f)) return true;
        return Vk.GetFenceStatus(Device, f.Fence) == Result.Success;
    }

    public void DestroyFence(FenceHandle fence)
    {
        if (!_fences.Remove(fence.Id, out var f)) return;
        Vk.DestroyFence(Device, f.Fence, null);
    }

    // ---- Helpers ----

    internal struct Staging : IDisposable
    {
        public VulkanDevice Dev;
        public Silk.NET.Vulkan.Buffer Buffer;
        public DeviceMemory Memory;

        public void Dispose()
        {
            Dev.Vk.DestroyBuffer(Dev.Device, Buffer, null);
            Dev.Vk.FreeMemory(Dev.Device, Memory, null);
        }
    }

    internal Staging CreateStaging(int size, ReadOnlySpan<byte> data)
    {
        var info = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = (ulong)size,
            Usage = BufferUsageFlags.TransferSrcBit,
            SharingMode = SharingMode.Exclusive,
        };
        Vk.CreateBuffer(Device, &info, null, out var buf).ThrowIfError();
        Vk.GetBufferMemoryRequirements(Device, buf, out var req);

        var alloc = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = req.Size,
            MemoryTypeIndex = FindMemoryType(req.MemoryTypeBits,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit),
        };
        Vk.AllocateMemory(Device, &alloc, null, out var mem).ThrowIfError();
        Vk.BindBufferMemory(Device, buf, mem, 0);

        if (!data.IsEmpty)
        {
            void* p = null;
            Vk.MapMemory(Device, mem, 0, (ulong)size, 0, ref p).ThrowIfError();
            fixed (byte* src = data) System.Buffer.MemoryCopy(src, p, size, data.Length);
            Vk.UnmapMemory(Device, mem);
        }

        return new Staging { Dev = this, Buffer = buf, Memory = mem };
    }

    internal void ExecuteImmediate(Action<CommandBuffer> record)
    {
        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = GraphicsPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };
        CommandBuffer cb;
        Vk.AllocateCommandBuffers(Device, &allocInfo, &cb).ThrowIfError();

        var begin = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        Vk.BeginCommandBuffer(cb, &begin);
        record(cb);
        Vk.EndCommandBuffer(cb);

        var submitInfo = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &cb,
        };
        Vk.QueueSubmit(GraphicsQueue, 1, &submitInfo, default).ThrowIfError();
        Vk.QueueWaitIdle(GraphicsQueue);

        Vk.FreeCommandBuffers(Device, GraphicsPool, 1, &cb);
    }

    internal void CopyBufferImmediate(Silk.NET.Vulkan.Buffer src, Silk.NET.Vulkan.Buffer dst, ulong size, ulong srcOff, ulong dstOff)
    {
        ExecuteImmediate(cb =>
        {
            var region = new BufferCopy(srcOff, dstOff, size);
            Vk.CmdCopyBuffer(cb, src, dst, 1, &region);
        });
    }

    internal void TransitionImageLayout(
        CommandBuffer cb, Image image, Format fmt,
        ImageLayout oldLayout, ImageLayout newLayout,
        uint baseMip, uint mipCount, uint baseLayer, uint layerCount)
    {
        var aspect = fmt is Format.D24UnormS8Uint or Format.D32SfloatS8Uint
            ? ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit
            : fmt is Format.D16Unorm or Format.D32Sfloat or Format.X8D24UnormPack32
                ? ImageAspectFlags.DepthBit
                : ImageAspectFlags.ColorBit;

        var barrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = new ImageSubresourceRange(aspect, baseMip, mipCount, baseLayer, layerCount),
        };

        PipelineStageFlags srcStage, dstStage;
        (barrier.SrcAccessMask, barrier.DstAccessMask, srcStage, dstStage) = (oldLayout, newLayout) switch
        {
            (ImageLayout.Undefined, ImageLayout.TransferDstOptimal) =>
                ((AccessFlags)0, AccessFlags.TransferWriteBit, PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit),
            (ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal) =>
                (AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit, PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit),
            (ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal) =>
                ((AccessFlags)0, AccessFlags.ColorAttachmentWriteBit, PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.ColorAttachmentOutputBit),
            (ImageLayout.Undefined, ImageLayout.DepthStencilAttachmentOptimal) =>
                ((AccessFlags)0, AccessFlags.DepthStencilAttachmentWriteBit, PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.EarlyFragmentTestsBit),
            (ImageLayout.ColorAttachmentOptimal, ImageLayout.PresentSrcKhr) =>
                (AccessFlags.ColorAttachmentWriteBit, (AccessFlags)0, PipelineStageFlags.ColorAttachmentOutputBit, PipelineStageFlags.BottomOfPipeBit),
            (ImageLayout.Undefined, ImageLayout.PresentSrcKhr) =>
                ((AccessFlags)0, (AccessFlags)0, PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.BottomOfPipeBit),
            // Swapchain image reused as a render target that PRESERVES contents (LoadOp.Load). The
            // common clear/discard path routes through (Undefined,ColorAttachment) instead; this arm
            // keeps the rare load case off the AllCommands full-flush catch-all.
            (ImageLayout.PresentSrcKhr, ImageLayout.ColorAttachmentOptimal) =>
                ((AccessFlags)0, AccessFlags.ColorAttachmentWriteBit,
                 PipelineStageFlags.ColorAttachmentOutputBit, PipelineStageFlags.ColorAttachmentOutputBit),
            // HDR pipeline: render to an offscreen color target, then sample it in the next pass
            // (lightmap → composite → bloom chain → tonemap). Without these explicit, tightly-scoped
            // cases the pair fell through to the AllCommands→AllCommands catch-all below — a full GPU
            // pipeline flush on every offscreen pass, which serializes the whole frame on tiled/iGPU
            // (the ~4× Vulkan-vs-OpenGL gap). Scope it to color-write → fragment-read instead.
            (ImageLayout.ColorAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal) =>
                (AccessFlags.ColorAttachmentWriteBit, AccessFlags.ShaderReadBit,
                 PipelineStageFlags.ColorAttachmentOutputBit, PipelineStageFlags.FragmentShaderBit),
            // Reverse: a target sampled last pass is reused as a render target (ping-ponged bloom
            // buffers, or any offscreen RT reused across frames).
            (ImageLayout.ShaderReadOnlyOptimal, ImageLayout.ColorAttachmentOptimal) =>
                (AccessFlags.ShaderReadBit, AccessFlags.ColorAttachmentWriteBit,
                 PipelineStageFlags.FragmentShaderBit, PipelineStageFlags.ColorAttachmentOutputBit),
            _ => (AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
                  AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
                  PipelineStageFlags.AllCommandsBit, PipelineStageFlags.AllCommandsBit),
        };

        Vk.CmdPipelineBarrier(cb, srcStage, dstStage, 0,
            0, null, 0, null, 1, &barrier);
    }

    internal static int BytesPerPixel(TextureFormat f) => f switch
    {
        TextureFormat.R8Unorm or TextureFormat.R8Snorm or TextureFormat.R8Uint or TextureFormat.R8Sint => 1,
        TextureFormat.R16Float or TextureFormat.R16Uint or TextureFormat.R16Sint => 2,
        TextureFormat.Rg8Unorm or TextureFormat.Rg8Snorm => 2,
        TextureFormat.Rgba8Unorm or TextureFormat.Rgba8UnormSrgb or TextureFormat.Rgba8Snorm
            or TextureFormat.Rgba8Uint or TextureFormat.Rgba8Sint
            or TextureFormat.Bgra8Unorm or TextureFormat.Bgra8UnormSrgb
            or TextureFormat.Rgb10A2Unorm or TextureFormat.Rg11B10Float
            or TextureFormat.R32Float or TextureFormat.R32Uint or TextureFormat.R32Sint
            or TextureFormat.Rg16Float or TextureFormat.Rg16Uint or TextureFormat.Rg16Sint => 4,
        TextureFormat.Rg32Float or TextureFormat.Rg32Uint or TextureFormat.Rg32Sint
            or TextureFormat.Rgba16Float or TextureFormat.Rgba16Uint or TextureFormat.Rgba16Sint => 8,
        TextureFormat.Rgba32Float or TextureFormat.Rgba32Uint or TextureFormat.Rgba32Sint => 16,
        _ => 4,
    };
}
