namespace Penelope.Backends.Switch;

public sealed unsafe partial class SwitchDevice
{
    // ---- Shaders ----

    public ShaderHandle CreateShader(ShaderSource source)
        => throw NvnNotImplemented(nameof(CreateShader),
            "NVN consumes SPIR-V directly via nvnProgramSetSubroutineLinkage / nvnProgramInitialize " +
            "with NVN_SHADER_STAGE_VERTEX|FRAGMENT|COMPUTE entries — no SPIRV-Cross step needed " +
            "(unlike OpenGL/Metal). Source.VertexSpirv goes straight into a NVNshaderData blob. " +
            "If only GLSL is provided, fall back through ShaderLib.CompileGlslToSpirv first.");

    public void DestroyShader(ShaderHandle shader)
        => throw NvnNotImplemented(nameof(DestroyShader), "nvnProgramFinalize on each stage.");

    // ---- Render pipelines ----

    public RenderPipelineHandle CreateRenderPipeline(in RenderPipelineDesc desc)
        => throw NvnNotImplemented(nameof(CreateRenderPipeline),
            "NVN doesn't have a single 'pipeline' object — instead, store the desc and apply " +
            "state piecewise at SetPipeline time: nvnCommandBufferBindProgram, BindVertexAttribState, " +
            "BindVertexStreamState, SetBlendStateBindings, SetDepthStencilState, SetCullMode, " +
            "SetFrontFace, SetPolygonMode. Cache the NVNvertexAttribState arrays on the pipeline " +
            "wrapper so SetPipeline is one bind per state object instead of per attribute.");

    public void DestroyRenderPipeline(RenderPipelineHandle pipeline)
        => throw NvnNotImplemented(nameof(DestroyRenderPipeline),
            "Free any per-pipeline state caches (vertex attrib state arrays).");

    public ComputePipelineHandle CreateComputePipeline(in ComputePipelineDesc desc)
        => throw NvnNotImplemented(nameof(CreateComputePipeline),
            "Compute pipelines on NVN are basically just a program ref + workgroup-size lookup. " +
            "Bind via nvnCommandBufferBindProgram(NVN_SHADER_STAGE_COMPUTE).");

    public void DestroyComputePipeline(ComputePipelineHandle pipeline)
        => throw NvnNotImplemented(nameof(DestroyComputePipeline), "Drop the wrapper.");

    // ---- Bind groups ----

    public BindGroupLayoutHandle CreateBindGroupLayout(in BindGroupLayoutDesc desc)
        => throw NvnNotImplemented(nameof(CreateBindGroupLayout),
            "NVN has no descriptor-set object — record the entries and use them at SetBindGroup " +
            "to dispatch nvnCommandBufferBindUniformBuffer / BindStorageBuffer / BindTexture / " +
            "BindSampler against the right binding slots.");

    public void DestroyBindGroupLayout(BindGroupLayoutHandle layout)
        => throw NvnNotImplemented(nameof(DestroyBindGroupLayout), "Drop the wrapper.");

    public BindGroupLayoutHandle GetBindGroupLayout(in BindGroupLayoutDesc desc)
        => throw NvnNotImplemented(nameof(GetBindGroupLayout),
            "Cache by BindGroupLayoutKey (entries[]) → CreateBindGroupLayout. Same shape as " +
            "the desktop backends; once CreateBindGroupLayout lands the cache code is identical.");

    public BindGroupHandle CreateBindGroup(in BindGroupDesc desc)
        => throw NvnNotImplemented(nameof(CreateBindGroup),
            "Record entries — NVN binds resources directly per draw-call via the command buffer.");

    public void DestroyBindGroup(BindGroupHandle group)
        => throw NvnNotImplemented(nameof(DestroyBindGroup), "Drop the wrapper.");
}
