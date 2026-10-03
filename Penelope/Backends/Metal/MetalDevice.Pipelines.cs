using System.Runtime.Versioning;
using SharpMetal.Foundation;
using SharpMetal.Metal;

namespace Penelope.Backends.Metal;

[SupportedOSPlatform("macos")]
public sealed unsafe partial class MetalDevice
{
    // ---- Shaders ----

    public ShaderHandle CreateShader(ShaderSource source)
    {
        // Prefer pre-baked MSL (PenelopeShaderBaker emits it at build time) — skips runtime
        // SPIRV-Cross. Fall back to runtime translation when only SPIR-V is present (tools,
        // tests, hot-reload). Plain-GLSL-only sources are unsupported because there's no
        // shipping pipeline that produces MSL from GLSL directly without going through SPIR-V.
        if (source.VertexGlsl != null && source.VertexSpirv == null && source.VertexMsl == null)
            throw new NotSupportedException(
                "Metal backend needs SPIR-V or pre-baked MSL — provide *Spirv or *Msl on the " +
                "ShaderSource (PenelopeShaderBaker emits both at build time).");

        var shader = new MetalShader();
        bool sawPushConstants = false;

        if (source.VertexMsl != null || source.VertexSpirv != null)
        {
            var (msl, mslEntry, hasPush) = ResolveMsl(source.VertexMsl, source.VertexSpirv, ShaderStage.Vertex);
            if (hasPush) sawPushConstants = true;
            (shader.VertexLibrary, shader.VertexFunction) = CompileMsl(msl, mslEntry ?? source.VertexEntry, source.DebugName, "vertex");
            shader.HasVertex = true;
        }
        if (source.FragmentMsl != null || source.FragmentSpirv != null)
        {
            var (msl, mslEntry, hasPush) = ResolveMsl(source.FragmentMsl, source.FragmentSpirv, ShaderStage.Fragment);
            if (hasPush) sawPushConstants = true;
            (shader.FragmentLibrary, shader.FragmentFunction) = CompileMsl(msl, mslEntry ?? source.FragmentEntry, source.DebugName, "fragment");
            shader.HasFragment = true;
        }
        if (source.ComputeMsl != null || source.ComputeSpirv != null)
        {
            var (msl, mslEntry, hasPush) = ResolveMsl(source.ComputeMsl, source.ComputeSpirv, ShaderStage.Compute);
            if (hasPush) sawPushConstants = true;
            (shader.ComputeLibrary, shader.ComputeFunction) = CompileMsl(msl, mslEntry ?? source.ComputeEntry, source.DebugName, "compute");
            shader.HasCompute = true;
        }
        shader.HasPushConstants = sawPushConstants;

        var id = NewHandleId();
        _shaders[id] = shader;
        return new ShaderHandle(id);
    }

    /// <summary>
    ///     Resolve MSL for <paramref name="stage"/> — prefer the pre-baked source if the build
    ///     emitted one, else fall back to runtime SPIRV-Cross translation. Either path returns
    ///     the source, the renamed entry-point name (SPIRV-Cross renames <c>main</c> to
    ///     <c>main0</c> because <c>main</c> is reserved in MSL), and a best-effort
    ///     push-constant indicator.
    /// </summary>
    private static (string Source, string? Entry, bool HasPush) ResolveMsl(string? bakedMsl, byte[]? spirv, ShaderStage stage)
    {
        string source;
        string? entry;
        if (bakedMsl != null)
        {
            source = bakedMsl;
            entry = ExtractMslEntry(source, stage);
        }
        else
        {
            var msl = ShaderLib.SpirvToMsl(spirv!);
            source = msl.Source;
            entry = stage switch
            {
                ShaderStage.Vertex => msl.VertexEntry,
                ShaderStage.Fragment => msl.FragmentEntry,
                ShaderStage.Compute => msl.ComputeEntry,
                _ => null,
            };
        }
        var hasPush = source.Contains("push_constant", StringComparison.Ordinal)
                      || source.Contains("[[buffer(", StringComparison.Ordinal); // best-effort signal
        return (source, entry, hasPush);
    }

    /// <summary>Find the MSL function name for the given stage qualifier — same regex as ShaderLib.</summary>
    private static string? ExtractMslEntry(string msl, ShaderStage stage)
    {
        var keyword = stage switch
        {
            ShaderStage.Vertex => "vertex",
            ShaderStage.Fragment => "fragment",
            ShaderStage.Compute => "kernel",
            _ => null,
        };
        if (keyword == null) return null;
        var pattern = new System.Text.RegularExpressions.Regex(
            $@"^\s*{keyword}\s+\S+\s+([A-Za-z_]\w*)\s*\(",
            System.Text.RegularExpressions.RegexOptions.Multiline);
        var m = pattern.Match(msl);
        return m.Success ? m.Groups[1].Value : null;
    }

    private (MTLLibrary lib, MTLFunction fn) CompileMsl(string msl, string entry, string? debugName, string stageLabel)
    {
        using var opts = new MTLCompileOptions();
        NSError err = default;
        var lib = Device.NewLibrary(msl, opts, ref err);
        if (lib.NativePtr == 0)
        {
            var msg = err.NativePtr != 0
                ? err.LocalizedDescription.ToString() ?? ""
                : "unknown";
            throw new InvalidOperationException(
                $"Metal {stageLabel} compile failed for '{debugName ?? "shader"}': {msg}\n--- MSL ---\n{msl}");
        }
        var fn = lib.NewFunction(entry);
        if (fn.NativePtr == 0)
        {
            lib.Dispose();
            throw new InvalidOperationException(
                $"Metal {stageLabel} entry '{entry}' not found for '{debugName ?? "shader"}'.\n--- MSL ---\n{msl}");
        }
        return (lib, fn);
    }

    public void DestroyShader(ShaderHandle shader)
    {
        if (!_shaders.Remove(shader.Id, out var sh)) return;
        if (sh.HasVertex) { sh.VertexFunction.Dispose(); sh.VertexLibrary.Dispose(); }
        if (sh.HasFragment) { sh.FragmentFunction.Dispose(); sh.FragmentLibrary.Dispose(); }
        if (sh.HasCompute) { sh.ComputeFunction.Dispose(); sh.ComputeLibrary.Dispose(); }
    }

    // ---- Render pipelines ----

    public RenderPipelineHandle CreateRenderPipeline(in RenderPipelineDesc desc)
    {
        var sh = _shaders[desc.Shader.Id];

        // Plain var (not `using`) so we can mutate properties below — Roslyn forbids modifying
        // members of a `using` variable.
        var pd = new MTLRenderPipelineDescriptor
        {
            VertexFunction = sh.VertexFunction,
            FragmentFunction = sh.FragmentFunction,
            RasterSampleCount = (ulong)Math.Max(1, desc.Multisample.SampleCount),
            AlphaToCoverageEnabled = desc.Multisample.AlphaToCoverageEnabled,
        };

        // Vertex layout — Metal numbers buffer slots from the END (highest indices) downward
        // so user vertex buffers don't clash with bind-group buffers (which start at 0). Common
        // SPIRV-Cross convention: vertex buffers at index 30 - slot.
        var vd = new MTLVertexDescriptor();
        foreach (var b in desc.VertexLayout.Buffers)
        {
            ulong mtlBufferIndex = MtlVertexBufferIndex(b.BufferSlot);
            var layout = vd.Layouts.Object(mtlBufferIndex);
            layout.Stride = (ulong)b.StrideBytes;
            layout.StepFunction = b.StepMode == VertexStepMode.PerInstance
                ? MTLVertexStepFunction.PerInstance
                : MTLVertexStepFunction.PerVertex;
            layout.StepRate = 1;
            foreach (var a in b.Attributes)
            {
                var attr = vd.Attributes.Object((ulong)a.ShaderLocation);
                attr.Format = MetalConvert.ToMetal(a.Format);
                attr.Offset = (ulong)a.OffsetBytes;
                attr.BufferIndex = mtlBufferIndex;
            }
        }
        pd.VertexDescriptor = vd;

        // Color attachments + per-attachment blend state.
        for (var i = 0; i < desc.ColorTargets.Length; i++)
        {
            var ct = desc.ColorTargets[i];
            var attach = pd.ColorAttachments.Object((ulong)i);
            attach.PixelFormat = MetalConvert.ToMetal(ct.Format);
            attach.BlendingEnabled = ct.Blend.Enabled;
            attach.SourceRGBBlendFactor = MetalConvert.ToMetal(ct.Blend.SrcColor);
            attach.DestinationRGBBlendFactor = MetalConvert.ToMetal(ct.Blend.DstColor);
            attach.RgbBlendOperation = MetalConvert.ToMetal(ct.Blend.ColorOp);
            attach.SourceAlphaBlendFactor = MetalConvert.ToMetal(ct.Blend.SrcAlpha);
            attach.DestinationAlphaBlendFactor = MetalConvert.ToMetal(ct.Blend.DstAlpha);
            attach.AlphaBlendOperation = MetalConvert.ToMetal(ct.Blend.AlphaOp);
            attach.WriteMask = MetalConvert.ToMetal(ct.Blend.WriteMask);
        }

        if (desc.DepthStencilFormat.HasValue)
        {
            var fmt = MetalConvert.ToMetal(desc.DepthStencilFormat.Value);
            pd.DepthAttachmentPixelFormat = fmt;
            if (desc.DepthStencilFormat.Value is TextureFormat.Depth24PlusStencil8
                                              or TextureFormat.Depth32FloatStencil8)
                pd.StencilAttachmentPixelFormat = fmt;
        }

        NSError err = default;
        var pipeline = Device.NewRenderPipelineState(pd, ref err);
        pd.Dispose();
        if (pipeline.NativePtr == 0)
        {
            var msg = err.NativePtr != 0
                ? err.LocalizedDescription.ToString() ?? ""
                : "unknown";
            throw new InvalidOperationException(
                $"NewRenderPipelineState failed for '{desc.DebugName ?? "pipeline"}': {msg}");
        }

        // Depth/stencil state is a separate Metal object (not part of the render pipeline).
        var ds = desc.DepthStencil;
        var hasDs = ds.DepthTestEnabled || ds.StencilEnabled || ds.DepthWriteEnabled;
        MTLDepthStencilState dsState = default;
        if (hasDs)
        {
            var dsd = new MTLDepthStencilDescriptor
            {
                DepthCompareFunction = MetalConvert.ToMetal(ds.DepthCompare),
                DepthWriteEnabled = ds.DepthWriteEnabled,
            };
            if (ds.StencilEnabled)
            {
                var front = new MTLStencilDescriptor
                {
                    StencilCompareFunction = MetalConvert.ToMetal(ds.StencilFront.Compare),
                    StencilFailureOperation = MetalConvert.ToMetal(ds.StencilFront.FailOp),
                    DepthFailureOperation = MetalConvert.ToMetal(ds.StencilFront.DepthFailOp),
                    DepthStencilPassOperation = MetalConvert.ToMetal(ds.StencilFront.PassOp),
                    ReadMask = ds.StencilReadMask,
                    WriteMask = ds.StencilWriteMask,
                };
                var back = new MTLStencilDescriptor
                {
                    StencilCompareFunction = MetalConvert.ToMetal(ds.StencilBack.Compare),
                    StencilFailureOperation = MetalConvert.ToMetal(ds.StencilBack.FailOp),
                    DepthFailureOperation = MetalConvert.ToMetal(ds.StencilBack.DepthFailOp),
                    DepthStencilPassOperation = MetalConvert.ToMetal(ds.StencilBack.PassOp),
                    ReadMask = ds.StencilReadMask,
                    WriteMask = ds.StencilWriteMask,
                };
                dsd.FrontFaceStencil = front;
                dsd.BackFaceStencil = back;
                front.Dispose();
                back.Dispose();
            }
            dsState = Device.NewDepthStencilState(dsd);
            dsd.Dispose();
        }

        var meta = new MetalRenderPipeline
        {
            Pipeline = pipeline,
            DepthStencilState = dsState,
            HasDepthStencilState = hasDs,
            Desc = desc,
            Topology = MetalConvert.ToMetal(desc.Topology),
            CullMode = MetalConvert.ToMetal(desc.Rasterizer.Cull),
            Winding = MetalConvert.ToMetal(desc.Rasterizer.FrontFace),
            FillMode = MetalConvert.ToMetal(desc.Rasterizer.Polygon),
            DepthClipEnabled = desc.Rasterizer.DepthClampEnabled,
            DepthBiasConstant = ds.DepthBiasConstant,
            DepthBiasSlope = ds.DepthBiasSlope,
            DepthBiasClamp = ds.DepthBiasClamp,
        };
        var id = NewHandleId();
        _renderPipelines[id] = meta;
        return new RenderPipelineHandle(id);
    }

    public void DestroyRenderPipeline(RenderPipelineHandle pipeline)
    {
        if (!_renderPipelines.Remove(pipeline.Id, out var p)) return;
        p.Pipeline.Dispose();
        if (p.HasDepthStencilState) p.DepthStencilState.Dispose();
    }

    public ComputePipelineHandle CreateComputePipeline(in ComputePipelineDesc desc)
    {
        var sh = _shaders[desc.Shader.Id];
        if (!sh.HasCompute) throw new ArgumentException("Shader has no compute stage.");
        NSError err = default;
        var pipeline = Device.NewComputePipelineState(sh.ComputeFunction, ref err);
        if (pipeline.NativePtr == 0)
        {
            var msg = err.NativePtr != 0
                ? err.LocalizedDescription.ToString() ?? ""
                : "unknown";
            throw new InvalidOperationException($"NewComputePipelineState failed: {msg}");
        }
        var meta = new MetalComputePipeline
        {
            Pipeline = pipeline,
            Desc = desc,
            ThreadsPerGroup = new MTLSize { width = 1, height = 1, depth = 1 },
        };
        var id = NewHandleId();
        _computePipelines[id] = meta;
        return new ComputePipelineHandle(id);
    }

    public void DestroyComputePipeline(ComputePipelineHandle pipeline)
    {
        if (!_computePipelines.Remove(pipeline.Id, out var p)) return;
        p.Pipeline.Dispose();
    }

    // ---- Bind groups ----

    public BindGroupLayoutHandle CreateBindGroupLayout(in BindGroupLayoutDesc desc)
    {
        var id = NewHandleId();
        _bindGroupLayouts[id] = new MetalBindGroupLayout { Entries = desc.Entries };
        return new BindGroupLayoutHandle(id);
    }

    public void DestroyBindGroupLayout(BindGroupLayoutHandle layout)
        => _bindGroupLayouts.Remove(layout.Id);

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
        var id = NewHandleId();
        _bindGroups[id] = new MetalBindGroup { LayoutId = desc.Layout.Id, Entries = desc.Entries };
        return new BindGroupHandle(id);
    }

    public void DestroyBindGroup(BindGroupHandle group)
        => _bindGroups.Remove(group.Id);

    /// <summary>
    ///     Map a Penelope vertex-buffer slot to a Metal vertex-stage [[buffer(N)]] index. Metal
    ///     only has one buffer namespace per stage, so we put vertex buffers at high indices
    ///     (counting down from 30) to leave low indices for user UBOs/SSBOs from the bind groups.
    /// </summary>
    internal static ulong MtlVertexBufferIndex(int penelopeSlot)
        => (ulong)(30 - penelopeSlot);

    /// <summary>
    ///     Reserved Metal buffer index for push-constant emulation. SPIRV-Cross's MSL emitter
    ///     places the push-constant block at a fixed slot — we mirror that here so SetVertexBytes
    ///     / SetFragmentBytes target the right index.
    /// </summary>
    internal const ulong PushConstantBufferIndex = 0;
}
