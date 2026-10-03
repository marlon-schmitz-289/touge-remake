using System.Reflection;

namespace Penelope;

/// <summary>
///     Loads SPIR-V shader bytes from embedded assembly resources produced by the glslc
///     MSBuild target in <c>Penelope.targets</c>. Shader names are source-file names without
///     the stage suffix: <c>LoadGraphics(asm, "sprite", "sprite")</c> reads
///     <c>Shaders/sprite.vert.spv</c> and <c>Shaders/sprite.frag.spv</c>.
/// </summary>
public static class ShaderLoader
{
    public static byte[] LoadSpirvRaw(Assembly asm, string logicalName)
    {
        using var s = asm.GetManifestResourceStream(logicalName)
            ?? throw new FileNotFoundException(
                $"Embedded SPIR-V not found: {logicalName}. " +
                $"Check that <PenelopeShader Include=\"Shaders\\...\"/> covers the source file " +
                $"and that the build target ran glslc successfully.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    public static ShaderSource LoadGraphics(Assembly asm, string vertexName, string fragmentName, string? debugName = null)
    {
        return new ShaderSource
        {
            VertexSpirv = LoadOrCompileStage(asm, vertexName, "vert", ShaderStage.Vertex),
            FragmentSpirv = LoadOrCompileStage(asm, fragmentName, "frag", ShaderStage.Fragment),
            VertexGlsl = LoadOptionalText(asm, $"Shaders/{vertexName}.vert.glsl"),
            FragmentGlsl = LoadOptionalText(asm, $"Shaders/{fragmentName}.frag.glsl"),
            VertexMsl = LoadOptionalText(asm, $"Shaders/{vertexName}.vert.msl"),
            FragmentMsl = LoadOptionalText(asm, $"Shaders/{fragmentName}.frag.msl"),
            DebugName = debugName ?? $"{vertexName}+{fragmentName}",
        };
    }

    public static ShaderSource LoadCompute(Assembly asm, string name, string? debugName = null)
    {
        return new ShaderSource
        {
            ComputeSpirv = LoadOrCompileStage(asm, name, "comp", ShaderStage.Compute),
            ComputeGlsl = LoadOptionalText(asm, $"Shaders/{name}.comp.glsl"),
            ComputeMsl = LoadOptionalText(asm, $"Shaders/{name}.comp.msl"),
            DebugName = debugName ?? name,
        };
    }

    /// <summary>
    ///     Load an embedded text resource if it exists, returning null otherwise. Used to pull
    ///     pre-baked GLSL/MSL variants populated by PenelopeShaderBaker — present in normal
    ///     builds, absent in tools/tests that hand-build a <see cref="ShaderSource"/>.
    /// </summary>
    private static string? LoadOptionalText(Assembly asm, string logicalName)
    {
        using var s = asm.GetManifestResourceStream(logicalName);
        if (s == null) return null;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    /// <summary>
    ///     Look up a build-time-compiled <c>.spv</c> resource; if absent, fall back to the
    ///     embedded GLSL source and compile it at runtime via glslang. This lets dev machines
    ///     without glslc / the Vulkan SDK still build and run — only one of the two resources
    ///     needs to be present.
    /// </summary>
    private static byte[] LoadOrCompileStage(Assembly asm, string baseName, string ext, ShaderStage stage)
    {
        var spvName = $"Shaders/{baseName}.{ext}.spv";
        using (var spv = asm.GetManifestResourceStream(spvName))
        {
            if (spv != null)
            {
                using var ms = new MemoryStream();
                spv.CopyTo(ms);
                return ms.ToArray();
            }
        }

        var glslName = $"Shaders/{baseName}.{ext}.glsl";
        using var glslStream = asm.GetManifestResourceStream(glslName)
            ?? throw new FileNotFoundException(
                $"Neither '{spvName}' nor '{glslName}' is embedded. " +
                $"Check the project's <PenelopeShader Include=\"Shaders\\{baseName}.{ext}\"/> entry.");
        using var reader = new StreamReader(glslStream);
        var source = reader.ReadToEnd();
        return ShaderLib.CompileGlslToSpirv(source, stage, $"{baseName}.{ext}");
    }

    /// <summary>
    ///     Read a GLSL source from an embedded resource and compile it to SPIR-V at runtime via
    ///     glslang. Use this when the build environment doesn't have glslc available (the
    ///     <c>PenelopeShader</c> MSBuild target needs the Vulkan SDK or system glslc; this path
    ///     bypasses that). The resulting SPIR-V is consumable by every backend (Vulkan directly,
    ///     OpenGL via SPIRV-Cross, Metal via SPIRV-Cross).
    /// </summary>
    public static ShaderSource LoadGraphicsFromGlsl(
        Assembly asm, string vertexResource, string fragmentResource, string? debugName = null)
    {
        var vertGlsl = ReadTextResource(asm, vertexResource);
        var fragGlsl = ReadTextResource(asm, fragmentResource);
        return new ShaderSource
        {
            VertexSpirv = ShaderLib.CompileGlslToSpirv(vertGlsl, ShaderStage.Vertex, vertexResource),
            FragmentSpirv = ShaderLib.CompileGlslToSpirv(fragGlsl, ShaderStage.Fragment, fragmentResource),
            DebugName = debugName ?? $"{vertexResource}+{fragmentResource}",
        };
    }

    /// <summary>Compile inline GLSL strings to SPIR-V — handy for tests and tools.</summary>
    public static ShaderSource GraphicsFromGlslSource(
        string vertexGlsl, string fragmentGlsl, string? debugName = null)
    {
        return new ShaderSource
        {
            VertexSpirv = ShaderLib.CompileGlslToSpirv(vertexGlsl, ShaderStage.Vertex, debugName),
            FragmentSpirv = ShaderLib.CompileGlslToSpirv(fragmentGlsl, ShaderStage.Fragment, debugName),
            DebugName = debugName,
        };
    }

    private static string ReadTextResource(Assembly asm, string logicalName)
    {
        using var s = asm.GetManifestResourceStream(logicalName)
            ?? throw new FileNotFoundException(
                $"Embedded GLSL not found: {logicalName}. Mark the file as <EmbeddedResource> with " +
                $"<LogicalName>{logicalName}</LogicalName>.");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
