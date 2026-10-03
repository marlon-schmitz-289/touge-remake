using System.Text;
using Silk.NET.OpenGL;

namespace Penelope.Backends.OpenGL;

public sealed unsafe partial class OpenGLDevice
{
    // ---- Shaders ----

    public ShaderHandle CreateShader(ShaderSource source)
    {
        // Penelope's source-of-truth shader format is SPIR-V (compiled from Vulkan-flavored GLSL
        // by the build target). For OpenGL we cross-compile to GLSL via SPIRV-Cross at shader
        // creation, then hand the resulting GLSL to the GL driver. Plain-GLSL shaders are accepted
        // too as an escape hatch — SPIR-V is preferred since it shares the upstream pipeline with
        // the Vulkan backend and avoids per-backend shader maintenance.
        var shader = new OpenGLShader();
        var program = Gl.CreateProgram();
        var attached = new List<uint>();
        bool sawPushConstants = false;
        string? pushBlockName = null;

        if (source.VertexSpirv != null || source.VertexGlsl != null)
        {
            var (glsl, pushName) = TranslateOrPassthrough(source.VertexSpirv, source.VertexGlsl);
            if (pushName != null) { sawPushConstants = true; pushBlockName ??= pushName; }
            var s = CompileStage(ShaderType.VertexShader, glsl, source.DebugName, "vertex");
            Gl.AttachShader(program, s);
            attached.Add(s);
            shader.HasVertex = true;
        }
        if (source.FragmentSpirv != null || source.FragmentGlsl != null)
        {
            var (glsl, pushName) = TranslateOrPassthrough(source.FragmentSpirv, source.FragmentGlsl);
            if (pushName != null) { sawPushConstants = true; pushBlockName ??= pushName; }
            var s = CompileStage(ShaderType.FragmentShader, glsl, source.DebugName, "fragment");
            Gl.AttachShader(program, s);
            attached.Add(s);
            shader.HasFragment = true;
        }
        if (source.ComputeSpirv != null || source.ComputeGlsl != null)
        {
            var (glsl, pushName) = TranslateOrPassthrough(source.ComputeSpirv, source.ComputeGlsl);
            if (pushName != null) { sawPushConstants = true; pushBlockName ??= pushName; }
            var s = CompileStage(ShaderType.ComputeShader, glsl, source.DebugName, "compute");
            Gl.AttachShader(program, s);
            attached.Add(s);
            shader.HasCompute = true;
        }

        Gl.LinkProgram(program);
        Gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out int linkOk);
        if (linkOk == 0)
        {
            var log = Gl.GetProgramInfoLog(program);
            foreach (var s in attached) Gl.DeleteShader(s);
            Gl.DeleteProgram(program);
            throw new InvalidOperationException(
                $"OpenGL program link failed for '{source.DebugName ?? "shader"}':\n{log}");
        }
        foreach (var s in attached)
        {
            Gl.DetachShader(program, s);
            Gl.DeleteShader(s);
        }

        shader.Program = program;
        shader.HasPushConstants = sawPushConstants;
        shader.PushConstantBlockName = pushBlockName;
        // SPIRV-Cross emits the rewritten push-constant block with `layout(binding = N)` set to
        // ShaderLib.PushConstantBinding (we apply the decoration during translation), so the
        // binding is baked into the program — no glUniformBlockBinding call needed here.

        var id = NewHandleId();
        _shaders[id] = shader;
        return new ShaderHandle(id);
    }

    /// <summary>
    ///     Resolve GLSL for this stage. Prefer pre-baked GLSL (PenelopeShaderBaker writes it
    ///     at build time alongside the SPIR-V) — skips runtime SPIRV-Cross. Fall back to
    ///     runtime SPIR-V translation when only SPIR-V is present (tools, tests, hot-reload).
    /// </summary>
    private static (string source, string? pushBlockName) TranslateOrPassthrough(byte[]? spirv, string? glsl)
    {
        if (glsl != null)
            return (glsl, null);
        if (spirv != null)
        {
            var t = ShaderLib.SpirvToGlsl(spirv);
            return (t.Source, t.PushConstantBlockName);
        }
        throw new InvalidOperationException("Neither GLSL nor SPIR-V provided for OpenGL shader stage.");
    }

    private uint CompileStage(ShaderType type, string src, string? debugName, string stageLabel)
    {
        var sh = Gl.CreateShader(type);
        Gl.ShaderSource(sh, src);
        Gl.CompileShader(sh);
        Gl.GetShader(sh, ShaderParameterName.CompileStatus, out int ok);
        if (ok == 0)
        {
            var log = Gl.GetShaderInfoLog(sh);
            Gl.DeleteShader(sh);
            // Number the source for easier log reading.
            var numbered = NumberLines(src);
            throw new InvalidOperationException(
                $"OpenGL {stageLabel} shader compile failed for '{debugName ?? "shader"}':\n{log}\n--- source ---\n{numbered}");
        }
        return sh;
    }

    private static string NumberLines(string s)
    {
        var sb = new StringBuilder(s.Length + 64);
        var lines = s.Split('\n');
        for (var i = 0; i < lines.Length; i++)
            sb.AppendLine($"{i + 1,4}: {lines[i].TrimEnd('\r')}");
        return sb.ToString();
    }

    public void DestroyShader(ShaderHandle shader)
    {
        if (!_shaders.Remove(shader.Id, out var sh)) return;
        Gl.DeleteProgram(sh.Program);
    }

    // ---- Render pipelines ----

    public RenderPipelineHandle CreateRenderPipeline(in RenderPipelineDesc desc)
    {
        var sh = _shaders[desc.Shader.Id];

        // VAO holds the vertex format + per-attribute → buffer-slot mapping; the actual
        // vertex buffer is bound per-draw via glBindVertexBuffer (separate vertex format,
        // GL 4.3+) so the pipeline VAO is reusable across buffer changes.
        uint vao = Gl.GenVertexArray();
        Gl.BindVertexArray(vao);

        foreach (var b in desc.VertexLayout.Buffers)
        {
            foreach (var a in b.Attributes)
            {
                var (type, components, normalized, isInteger) = OpenGLConvert.ToGl(a.Format);
                Gl.EnableVertexAttribArray((uint)a.ShaderLocation);
                if (isInteger)
                {
                    // VertexAttribIFormat expects VertexAttribIType — same numeric values, separate enum.
                    Gl.VertexAttribIFormat((uint)a.ShaderLocation, components, (VertexAttribIType)type, (uint)a.OffsetBytes);
                }
                else
                {
                    Gl.VertexAttribFormat((uint)a.ShaderLocation, components, (VertexAttribType)type, normalized, (uint)a.OffsetBytes);
                }
                Gl.VertexAttribBinding((uint)a.ShaderLocation, (uint)b.BufferSlot);
            }
            Gl.VertexBindingDivisor((uint)b.BufferSlot, b.StepMode == VertexStepMode.PerInstance ? 1u : 0u);
        }

        Gl.BindVertexArray(0);

        var pipeline = new OpenGLRenderPipeline
        {
            Program = sh.Program,
            Vao = vao,
            Desc = desc,
            Topology = OpenGLConvert.ToGl(desc.Topology),
            HasPushConstants = sh.HasPushConstants,
        };
        var id = NewHandleId();
        _renderPipelines[id] = pipeline;
        return new RenderPipelineHandle(id);
    }

    public void DestroyRenderPipeline(RenderPipelineHandle pipeline)
    {
        if (!_renderPipelines.Remove(pipeline.Id, out var p)) return;
        if (p.Vao != 0) Gl.DeleteVertexArray(p.Vao);
        // Program is owned by the shader handle, not the pipeline.
    }

    public ComputePipelineHandle CreateComputePipeline(in ComputePipelineDesc desc)
    {
        var sh = _shaders[desc.Shader.Id];
        if (!sh.HasCompute) throw new ArgumentException("Shader has no compute stage.");

        var pipeline = new OpenGLComputePipeline
        {
            Program = sh.Program,
            Desc = desc,
            HasPushConstants = sh.HasPushConstants,
        };
        var id = NewHandleId();
        _computePipelines[id] = pipeline;
        return new ComputePipelineHandle(id);
    }

    public void DestroyComputePipeline(ComputePipelineHandle pipeline)
    {
        _computePipelines.Remove(pipeline.Id);
    }

    // ---- Bind groups ----

    public BindGroupLayoutHandle CreateBindGroupLayout(in BindGroupLayoutDesc desc)
    {
        var layout = new OpenGLBindGroupLayout { Entries = desc.Entries };
        var id = NewHandleId();
        _bindGroupLayouts[id] = layout;
        return new BindGroupLayoutHandle(id);
    }

    public void DestroyBindGroupLayout(BindGroupLayoutHandle layout)
    {
        _bindGroupLayouts.Remove(layout.Id);
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
        // GL has no bind-group object — we just record the entries and apply them at SetBindGroup.
        var bg = new OpenGLBindGroup
        {
            LayoutId = desc.Layout.Id,
            Entries = desc.Entries,
        };
        var id = NewHandleId();
        _bindGroups[id] = bg;
        return new BindGroupHandle(id);
    }

    public void DestroyBindGroup(BindGroupHandle group)
    {
        _bindGroups.Remove(group.Id);
    }
}
