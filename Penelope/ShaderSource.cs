namespace Penelope;

/// <summary>
///     Shader source for the stages of a pipeline. Backends select the representation they need:
///     OpenGL compiles GLSL; Vulkan/Metal prefer SPIR-V and may cross-compile GLSL at load time
///     if SPIR-V is not provided.
///
///     For a render pipeline set <see cref="VertexGlsl"/>/<see cref="VertexSpirv"/> and the
///     fragment pair. For a compute pipeline set only the compute pair.
/// </summary>
public sealed class ShaderSource
{
    public string? VertexGlsl { get; init; }
    public string? FragmentGlsl { get; init; }
    public string? ComputeGlsl { get; init; }

    public byte[]? VertexSpirv { get; init; }
    public byte[]? FragmentSpirv { get; init; }
    public byte[]? ComputeSpirv { get; init; }

    /// <summary>
    ///     Pre-baked Metal Shading Language for the macOS backend, produced at build time by
    ///     PenelopeShaderBaker (SPIRV-Cross translation). When present, the Metal backend uses
    ///     this directly and skips the runtime SPIRV-Cross call.
    /// </summary>
    public string? VertexMsl { get; init; }
    public string? FragmentMsl { get; init; }
    public string? ComputeMsl { get; init; }

    /// <summary>Entry point. Defaults to <c>main</c>.</summary>
    public string VertexEntry { get; init; } = "main";
    public string FragmentEntry { get; init; } = "main";
    public string ComputeEntry { get; init; } = "main";

    /// <summary>Human-readable tag for debug/profiling (RenderDoc, NSight).</summary>
    public string? DebugName { get; init; }

    public static ShaderSource Graphics(string vertex, string fragment, string? debugName = null)
    {
        return new ShaderSource
        {
            VertexGlsl = vertex,
            FragmentGlsl = fragment,
            DebugName = debugName,
        };
    }

    public static ShaderSource GraphicsSpirv(byte[] vertex, byte[] fragment, string? debugName = null)
    {
        return new ShaderSource
        {
            VertexSpirv = vertex,
            FragmentSpirv = fragment,
            DebugName = debugName,
        };
    }

    public static ShaderSource Compute(string source, string? debugName = null)
    {
        return new ShaderSource
        {
            ComputeGlsl = source,
            DebugName = debugName,
        };
    }
}
