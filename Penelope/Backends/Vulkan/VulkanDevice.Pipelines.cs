using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace Penelope.Backends.Vulkan;

public sealed unsafe partial class VulkanDevice
{
    public RenderPipelineHandle CreateRenderPipeline(in RenderPipelineDesc desc)
    {
        if (!_shaders.TryGetValue(desc.Shader.Id, out var shader))
            throw new ArgumentException("Unknown shader.");

        // Pipeline layout
        var layout = CreatePipelineLayout(desc.BindGroupLayouts, desc.PushConstants);

        // Shader stages
        var vertEntry = SilkMarshal.StringToPtr(shader.VertexEntry);
        var fragEntry = SilkMarshal.StringToPtr(shader.FragmentEntry);
        var stages = stackalloc PipelineShaderStageCreateInfo[2];
        stages[0] = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.VertexBit,
            Module = shader.VertexModule,
            PName = (byte*)vertEntry,
        };
        stages[1] = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.FragmentBit,
            Module = shader.FragmentModule,
            PName = (byte*)fragEntry,
        };

        // Vertex input
        var bindings = new VertexInputBindingDescription[desc.VertexLayout.Buffers.Length];
        var attrCount = 0;
        foreach (var b in desc.VertexLayout.Buffers) attrCount += b.Attributes.Length;
        var attrs = new VertexInputAttributeDescription[attrCount];

        var attrI = 0;
        for (var i = 0; i < desc.VertexLayout.Buffers.Length; i++)
        {
            var b = desc.VertexLayout.Buffers[i];
            bindings[i] = new VertexInputBindingDescription
            {
                Binding = (uint)b.BufferSlot,
                Stride = (uint)b.StrideBytes,
                InputRate = b.StepMode == VertexStepMode.PerInstance
                    ? VertexInputRate.Instance
                    : VertexInputRate.Vertex,
            };
            foreach (var a in b.Attributes)
            {
                var (fmt, _) = VulkanConvert.ToVk(a.Format);
                attrs[attrI++] = new VertexInputAttributeDescription
                {
                    Location = (uint)a.ShaderLocation,
                    Binding = (uint)b.BufferSlot,
                    Format = fmt,
                    Offset = (uint)a.OffsetBytes,
                };
            }
        }

        fixed (VertexInputBindingDescription* pBind = bindings)
        fixed (VertexInputAttributeDescription* pAttr = attrs)
        {
            var vertexInput = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = (uint)bindings.Length,
                PVertexBindingDescriptions = pBind,
                VertexAttributeDescriptionCount = (uint)attrs.Length,
                PVertexAttributeDescriptions = pAttr,
            };

            var inputAssembly = new PipelineInputAssemblyStateCreateInfo
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = VulkanConvert.ToVk(desc.Topology),
                PrimitiveRestartEnable = false,
            };

            var viewportState = new PipelineViewportStateCreateInfo
            {
                SType = StructureType.PipelineViewportStateCreateInfo,
                ViewportCount = 1,
                ScissorCount = 1,
            };

            var raster = desc.Rasterizer;
            var rasterState = new PipelineRasterizationStateCreateInfo
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                DepthClampEnable = raster.DepthClampEnabled,
                RasterizerDiscardEnable = false,
                PolygonMode = VulkanConvert.ToVk(raster.Polygon),
                LineWidth = raster.LineWidth,
                CullMode = VulkanConvert.ToVk(raster.Cull),
                FrontFace = VulkanConvert.ToVk(raster.FrontFace),
                DepthBiasEnable = desc.DepthStencil.DepthBiasConstant != 0 || desc.DepthStencil.DepthBiasSlope != 0,
                DepthBiasConstantFactor = desc.DepthStencil.DepthBiasConstant,
                DepthBiasSlopeFactor = desc.DepthStencil.DepthBiasSlope,
                DepthBiasClamp = desc.DepthStencil.DepthBiasClamp,
            };

            var multisample = new PipelineMultisampleStateCreateInfo
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = VulkanConvert.ToVk(desc.Multisample.SampleCount),
                AlphaToCoverageEnable = desc.Multisample.AlphaToCoverageEnabled,
            };

            // Color blend (per attachment)
            var blendAttachments = new PipelineColorBlendAttachmentState[desc.ColorTargets.Length];
            for (var i = 0; i < desc.ColorTargets.Length; i++)
            {
                var b = desc.ColorTargets[i].Blend;
                blendAttachments[i] = new PipelineColorBlendAttachmentState
                {
                    BlendEnable = b.Enabled,
                    SrcColorBlendFactor = VulkanConvert.ToVk(b.SrcColor),
                    DstColorBlendFactor = VulkanConvert.ToVk(b.DstColor),
                    ColorBlendOp = VulkanConvert.ToVk(b.ColorOp),
                    SrcAlphaBlendFactor = VulkanConvert.ToVk(b.SrcAlpha),
                    DstAlphaBlendFactor = VulkanConvert.ToVk(b.DstAlpha),
                    AlphaBlendOp = VulkanConvert.ToVk(b.AlphaOp),
                    ColorWriteMask = VulkanConvert.ToVk(b.WriteMask),
                };
            }

            fixed (PipelineColorBlendAttachmentState* pBlend = blendAttachments)
            {
                var colorBlend = new PipelineColorBlendStateCreateInfo
                {
                    SType = StructureType.PipelineColorBlendStateCreateInfo,
                    LogicOpEnable = false,
                    AttachmentCount = (uint)blendAttachments.Length,
                    PAttachments = pBlend,
                };

                // Depth / stencil
                var ds = desc.DepthStencil;
                var depthStencil = new PipelineDepthStencilStateCreateInfo
                {
                    SType = StructureType.PipelineDepthStencilStateCreateInfo,
                    DepthTestEnable = ds.DepthTestEnabled,
                    DepthWriteEnable = ds.DepthWriteEnabled,
                    DepthCompareOp = VulkanConvert.ToVk(ds.DepthCompare),
                    DepthBoundsTestEnable = false,
                    StencilTestEnable = ds.StencilEnabled,
                    Front = new StencilOpState
                    {
                        CompareOp = VulkanConvert.ToVk(ds.StencilFront.Compare),
                        FailOp = VulkanConvert.ToVk(ds.StencilFront.FailOp),
                        DepthFailOp = VulkanConvert.ToVk(ds.StencilFront.DepthFailOp),
                        PassOp = VulkanConvert.ToVk(ds.StencilFront.PassOp),
                        CompareMask = ds.StencilReadMask,
                        WriteMask = ds.StencilWriteMask,
                        Reference = 0,
                    },
                    Back = new StencilOpState
                    {
                        CompareOp = VulkanConvert.ToVk(ds.StencilBack.Compare),
                        FailOp = VulkanConvert.ToVk(ds.StencilBack.FailOp),
                        DepthFailOp = VulkanConvert.ToVk(ds.StencilBack.DepthFailOp),
                        PassOp = VulkanConvert.ToVk(ds.StencilBack.PassOp),
                        CompareMask = ds.StencilReadMask,
                        WriteMask = ds.StencilWriteMask,
                        Reference = 0,
                    },
                };

                // Dynamic state
                var dynStates = stackalloc DynamicState[]
                {
                    DynamicState.Viewport,
                    DynamicState.Scissor,
                    DynamicState.BlendConstants,
                    DynamicState.StencilReference,
                };
                var dynState = new PipelineDynamicStateCreateInfo
                {
                    SType = StructureType.PipelineDynamicStateCreateInfo,
                    DynamicStateCount = 4,
                    PDynamicStates = dynStates,
                };

                // Dynamic rendering attachment formats (VK 1.3)
                var colorFormats = new Format[desc.ColorTargets.Length];
                for (var i = 0; i < desc.ColorTargets.Length; i++)
                    colorFormats[i] = VulkanConvert.ToVk(desc.ColorTargets[i].Format);

                fixed (Format* pFmt = colorFormats)
                {
                    var depthFmt = desc.DepthStencilFormat.HasValue
                        ? VulkanConvert.ToVk(desc.DepthStencilFormat.Value)
                        : Format.Undefined;
                    var stencilFmt = desc.DepthStencilFormat is TextureFormat.Depth24PlusStencil8 or TextureFormat.Depth32FloatStencil8
                        ? depthFmt : Format.Undefined;

                    var rendering = new PipelineRenderingCreateInfo
                    {
                        SType = StructureType.PipelineRenderingCreateInfo,
                        ColorAttachmentCount = (uint)colorFormats.Length,
                        PColorAttachmentFormats = pFmt,
                        DepthAttachmentFormat = depthFmt,
                        StencilAttachmentFormat = stencilFmt,
                    };

                    var info = new GraphicsPipelineCreateInfo
                    {
                        SType = StructureType.GraphicsPipelineCreateInfo,
                        PNext = &rendering,
                        StageCount = 2,
                        PStages = stages,
                        PVertexInputState = &vertexInput,
                        PInputAssemblyState = &inputAssembly,
                        PViewportState = &viewportState,
                        PRasterizationState = &rasterState,
                        PMultisampleState = &multisample,
                        PDepthStencilState = &depthStencil,
                        PColorBlendState = &colorBlend,
                        PDynamicState = &dynState,
                        Layout = layout,
                        RenderPass = default, // dynamic rendering
                        Subpass = 0,
                    };

                    Pipeline pipeline;
                    Vk.CreateGraphicsPipelines(Device, default, 1, &info, null, &pipeline).ThrowIfError();

                    SilkMarshal.Free(vertEntry);
                    SilkMarshal.Free(fragEntry);

                    var id = NewHandleId();
                    _renderPipelines[id] = new VulkanRenderPipeline
                    {
                        Pipeline = pipeline,
                        Layout = layout,
                        BindGroupLayoutIds = desc.BindGroupLayouts.Select(h => h.Id).ToArray(),
                        PushConstants = desc.PushConstants,
                        Desc = desc,
                    };
                    return new RenderPipelineHandle(id);
                }
            }
        }
    }

    public void DestroyRenderPipeline(RenderPipelineHandle pipeline)
    {
        if (!_renderPipelines.Remove(pipeline.Id, out var p)) return;
        Vk.DestroyPipeline(Device, p.Pipeline, null);
        Vk.DestroyPipelineLayout(Device, p.Layout, null);
    }

    public ComputePipelineHandle CreateComputePipeline(in ComputePipelineDesc desc)
    {
        if (!_shaders.TryGetValue(desc.Shader.Id, out var shader))
            throw new ArgumentException("Unknown shader.");
        if (!shader.HasCompute)
            throw new ArgumentException("Shader has no compute stage.");

        var layout = CreatePipelineLayout(desc.BindGroupLayouts, desc.PushConstants);
        var entry = SilkMarshal.StringToPtr(shader.ComputeEntry);

        var stage = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.ComputeBit,
            Module = shader.ComputeModule,
            PName = (byte*)entry,
        };

        var info = new ComputePipelineCreateInfo
        {
            SType = StructureType.ComputePipelineCreateInfo,
            Stage = stage,
            Layout = layout,
        };
        Pipeline pipeline;
        Vk.CreateComputePipelines(Device, default, 1, &info, null, &pipeline).ThrowIfError();
        SilkMarshal.Free(entry);

        var id = NewHandleId();
        _computePipelines[id] = new VulkanComputePipeline
        {
            Pipeline = pipeline,
            Layout = layout,
            BindGroupLayoutIds = desc.BindGroupLayouts.Select(h => h.Id).ToArray(),
            PushConstants = desc.PushConstants,
        };
        return new ComputePipelineHandle(id);
    }

    public void DestroyComputePipeline(ComputePipelineHandle pipeline)
    {
        if (!_computePipelines.Remove(pipeline.Id, out var p)) return;
        Vk.DestroyPipeline(Device, p.Pipeline, null);
        Vk.DestroyPipelineLayout(Device, p.Layout, null);
    }

    private PipelineLayout CreatePipelineLayout(
        BindGroupLayoutHandle[] bindGroupLayouts, PushConstantRange[] pushConstants)
    {
        foreach (var r in pushConstants)
            if (r.OffsetBytes + r.SizeBytes > _deviceProps.Limits.MaxPushConstantsSize)
                throw new NotSupportedException($"Push constants {r.OffsetBytes}+{r.SizeBytes} B exceed this GPU's " +
                    $"{_deviceProps.Limits.MaxPushConstantsSize} B (Vulkan guarantees 128): use a uniform buffer.");
        var setLayouts = new DescriptorSetLayout[bindGroupLayouts.Length];
        for (var i = 0; i < bindGroupLayouts.Length; i++)
            setLayouts[i] = _bindGroupLayouts[bindGroupLayouts[i].Id].Layout;

        var pushRanges = new Silk.NET.Vulkan.PushConstantRange[pushConstants.Length];
        for (var i = 0; i < pushConstants.Length; i++)
        {
            pushRanges[i] = new Silk.NET.Vulkan.PushConstantRange
            {
                StageFlags = VulkanConvert.ToVk(pushConstants[i].Visibility),
                Offset = (uint)pushConstants[i].OffsetBytes,
                Size = (uint)pushConstants[i].SizeBytes,
            };
        }

        fixed (DescriptorSetLayout* pSets = setLayouts)
        fixed (Silk.NET.Vulkan.PushConstantRange* pPush = pushRanges)
        {
            var info = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = (uint)setLayouts.Length,
                PSetLayouts = pSets,
                PushConstantRangeCount = (uint)pushRanges.Length,
                PPushConstantRanges = pPush,
            };
            Vk.CreatePipelineLayout(Device, &info, null, out var layout).ThrowIfError();
            return layout;
        }
    }

    internal VulkanRenderPipeline GetRenderPipeline(RenderPipelineHandle h) => _renderPipelines[h.Id];
    internal VulkanComputePipeline GetComputePipeline(ComputePipelineHandle h) => _computePipelines[h.Id];
    internal VulkanBindGroup GetBindGroup(BindGroupHandle h) => _bindGroups[h.Id];
    internal VulkanBuffer GetBuffer(BufferHandle h) => _buffers[h.Id];
    internal VulkanImage GetImage(ulong id) => _images[id];
    internal VulkanImageView GetImageView(TextureViewHandle h) => _imageViews[h.Id];
    internal VulkanQueryPool GetQueryPool(QueryPoolHandle h) => _queryPools[h.Id];
}
