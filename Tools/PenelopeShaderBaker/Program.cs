using Glslang.NET;
using SPIRVCross.NET;
using SPIRVCross.NET.GLSL;
using SPIRVCross.NET.MSL;
using GlslangShaderStage = Glslang.NET.ShaderStage;
using GlslangProgram = Glslang.NET.Program;
using GlslangShader = Glslang.NET.Shader;

namespace PenelopeShaderBaker;

/// <summary>
///     Build-time shader baker. Reads a single Vulkan-flavored GLSL 450 source file and emits:
///       <c>&lt;out-dir&gt;/&lt;base&gt;.&lt;stage&gt;.spv</c>  — SPIR-V (Vulkan)
///       <c>&lt;out-dir&gt;/&lt;base&gt;.&lt;stage&gt;.glsl</c> — OpenGL 4.5 core GLSL
///       <c>&lt;out-dir&gt;/&lt;base&gt;.&lt;stage&gt;.msl</c>  — Metal Shading Language (macOS)
///
///     <para>Invoked from <c>Penelope.targets</c> per shader. The runtime backends read the
///     embedded blob matching their backend; no glslc / SPIRV-Cross is needed at app startup.</para>
///
///     Usage: <c>PenelopeShaderBaker &lt;source-path&gt; &lt;out-dir&gt;</c>
///     Stage is inferred from the source file extension (.vert / .frag / .comp).
/// </summary>
internal static class Program
{
    private const int SpirvTargetVersion = 0x10300; // SPV 1.3 — see comment in Bake().

    public static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: PenelopeShaderBaker <source-path> <out-dir>");
            return 2;
        }

        var sourcePath = args[0];
        var outDir = args[1];

        try
        {
            Bake(sourcePath, outDir);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"PenelopeShaderBaker: {sourcePath}: {ex.Message}");
            return 1;
        }
    }

    private static void Bake(string sourcePath, string outDir)
    {
        var ext = Path.GetExtension(sourcePath).TrimStart('.').ToLowerInvariant();
        var stage = ext switch
        {
            "vert" => GlslangShaderStage.Vertex,
            "frag" => GlslangShaderStage.Fragment,
            "comp" => GlslangShaderStage.Compute,
            _ => throw new ArgumentException($"Unknown shader stage extension '.{ext}' (expected .vert/.frag/.comp)"),
        };

        // `#include "file"` (relative to the source) is pasted in textually; list included files as
        // <PenelopeShaderInclude> so edits to them trigger a re-bake.
        var source = System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(sourcePath), "^#include \"([^\"]+)\"",
            m => File.ReadAllText(Path.Combine(Path.GetDirectoryName(sourcePath)!, m.Groups[1].Value)),
            System.Text.RegularExpressions.RegexOptions.Multiline);
        var spirv = CompileToSpirv(source, stage, sourcePath);

        // Bake names mirror the existing glslc convention: "<base>.<ext>.spv" so existing
        // ShaderLoader code that reads "Shaders/sprite.vert.spv" keeps working.
        var baseName = Path.GetFileName(sourcePath); // includes ".vert" etc.
        Directory.CreateDirectory(outDir);
        File.WriteAllBytes(Path.Combine(outDir, $"{baseName}.spv"), spirv);

        // SPIR-V → GLSL (OpenGL 4.5 core) and SPIR-V → MSL (macOS) via SPIRV-Cross. Lock so
        // both translations share the same SPIRVCross context — matches the existing runtime
        // ShaderLib pattern.
        var (glsl, msl) = CrossTranslate(spirv);
        File.WriteAllText(Path.Combine(outDir, $"{baseName}.glsl"), glsl);
        File.WriteAllText(Path.Combine(outDir, $"{baseName}.msl"), msl);
    }

    private static byte[] CompileToSpirv(string glsl, GlslangShaderStage stage, string sourceName)
    {
        // Target Vulkan 1.1 / SPIR-V 1.3 — same rationale as the legacy targets file: Vulkan
        // 1.2+ compiles `discard` to OpDemoteToHelperInvocation which SPIRV-Cross can't emit
        // when targeting non-Vulkan GLSL. SPIR-V 1.3 keeps `discard` as classic OpKill, which
        // both desktop backends consume cleanly; Vulkan 1.3 runs SPIR-V 1.3 modules without
        // complaint.
        var input = new CompilationInput
        {
            language = SourceType.GLSL,
            stage = stage,
            client = ClientType.Vulkan,
            clientVersion = TargetClientVersion.Vulkan_1_1,
            targetLanguage = TargetLanguage.SPV,
            targetLanguageVersion = TargetLanguageVersion.SPV_1_3,
            code = glsl,
            defaultVersion = 450,
            defaultProfile = ShaderProfile.None,
            forceDefaultVersionAndProfile = false,
            forwardCompatible = false,
            messages = MessageType.Default | MessageType.SpvRules | MessageType.VulkanRules,
            resourceLimits = ResourceLimits.DefaultResource,
        };

        using var shader = new GlslangShader(input);
        if (!shader.Preprocess())
            throw new InvalidOperationException(
                $"glslang preprocess failed for '{sourceName}':\n{shader.GetInfoLog()}\n{shader.GetDebugLog()}");
        if (!shader.Parse())
            throw new InvalidOperationException(
                $"glslang parse failed for '{sourceName}':\n{shader.GetInfoLog()}\n{shader.GetDebugLog()}");

        using var program = new GlslangProgram();
        program.AddShader(shader);
        if (!program.Link(MessageType.Default | MessageType.SpvRules | MessageType.VulkanRules))
            throw new InvalidOperationException(
                $"glslang link failed for '{sourceName}':\n{program.GetInfoLog()}\n{program.GetDebugLog()}");

        if (!program.GenerateSPIRV(out var words, stage))
            throw new InvalidOperationException(
                $"glslang SPIR-V generation failed for '{sourceName}':\n{program.GetInfoLog()}\n{program.GetDebugLog()}");

        var bytes = new byte[words.Length * 4];
        Buffer.BlockCopy(words, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static (string Glsl, string Msl) CrossTranslate(byte[] spirv)
    {
        var ctx = new Context();

        // OpenGL 4.5 core. Push-constants surface as a std140 UBO — backend binds at
        // slot 0 (matches the legacy ShaderLib.PushConstantBinding convention).
        var glslCompiler = ctx.CreateGLSLCompiler(spirv);
        var resources = glslCompiler.CreateShaderResources();
        foreach (var pc in resources.PushConstantBuffers)
        {
            glslCompiler.SetDecoration(pc.id, Decoration.DescriptorSet, 0);
            glslCompiler.SetDecoration(pc.id, Decoration.Binding, 0);
            break; // SPIR-V allows at most one push-constant block per stage.
        }
        glslCompiler.glslOptions = new GLSLCompilerOptions
        {
            version = 450,
            ES = false,
            vulkanSemantics = false,
            emitPushConstantAsUniformBuffer = true,
            enable420PackExtension = true,
            separateShaderObjects = false,
        };
        var glsl = glslCompiler.Compile();

        // MSL. SPIRV-Cross renames `main` to `main0` because `main` is reserved in MSL —
        // the runtime Metal backend scans the emitted source for [[vertex]] / [[fragment]] /
        // [[kernel]] to recover the entry-point name.
        var mslCompiler = ctx.CreateMSLCompiler(spirv);
        mslCompiler.metalOptions = new MSLCompilerOptions
        {
            platform = Platform.MacOS,
            msl_version = (2, 3, 0),
            // [[texture(n)]]/[[sampler(n)]] = GLSL binding n, which is the slot the Metal encoder binds
            // (default numbering follows first use and breaks shaders whose bindings are not used in order)
            enableDecorationBinding = true,
        };
        var msl = mslCompiler.Compile();

        return (glsl, msl);
    }
}
