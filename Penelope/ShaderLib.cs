using Glslang.NET;
using SPIRVCross.NET;
using SPIRVCross.NET.GLSL;
using SPIRVCross.NET.MSL;
using GlslangShaderStage = Glslang.NET.ShaderStage;

namespace Penelope;

/// <summary>
///     Cross-backend shader translation backed by Khronos SPIRV-Cross. Penelope's source-of-truth
///     shader format is Vulkan-flavored GLSL 450 compiled to SPIR-V at build time (see
///     <c>Penelope.targets</c>). This class is the single funnel through which non-Vulkan backends
///     (OpenGL today, Metal next) consume that SPIR-V — Vulkan keeps using it directly.
///
///     SPIRV-Cross handles the translation properly: descriptor-set unification, push-constant
///     UBO emission, opaque sampler/image typing, layout(std140/std430), I/O block flattening,
///     etc. Doing this with regex would silently corrupt anything more complex than a triangle.
///
///     <para>The <see cref="Context"/> caches a single SPIRV-Cross instance per process. It is
///     thread-safe by virtue of the contained methods all being short-lived and stateless;
///     however the lock is per-call, not per-translation, so callers should treat translation as
///     a one-shot operation per shader (cache the result).</para>
/// </summary>
public static class ShaderLib
{
    private static readonly Lock SyncRoot = new();
    private static Context? _context;

    private static Context GetContext()
    {
        // Lazy init so apps that only use the Vulkan backend never load the native SPIRV-Cross lib.
        lock (SyncRoot)
        {
            return _context ??= new Context();
        }
    }

    /// <summary>
    ///     Translate SPIR-V to OpenGL 4.5 core compatible GLSL. Push constants are emitted as a
    ///     std140 UBO at <see cref="PushConstantBinding"/> (decoration applied here via SPIRV-Cross
    ///     reflection so the GL backend can bind a single device-wide UBO at that slot).
    ///
    ///     Returns the translated source plus the rewritten push-constant block name (or null if
    ///     the source declared none) so callers can resolve the GL block index for binding.
    /// </summary>
    public static GlslOutput SpirvToGlsl(byte[] spirv, int glslVersion = 450)
    {
        lock (SyncRoot)
        {
            var ctx = GetContext();
            var compiler = ctx.CreateGLSLCompiler(spirv);

            // Assign our reserved binding (and descriptor set 0) to any push-constant block before
            // compilation. With emitPushConstantAsUniformBuffer this surfaces in the output GLSL
            // as `layout(std140, binding = N) uniform NAME { ... }`, ready for glBindBufferBase.
            string? pushBlockName = null;
            var resources = compiler.CreateShaderResources();
            foreach (var pc in resources.PushConstantBuffers)
            {
                compiler.SetDecoration(pc.id, Decoration.DescriptorSet, 0);
                compiler.SetDecoration(pc.id, Decoration.Binding, (uint)PushConstantBinding);
                // Block (struct type) name is what GL needs for glGetUniformBlockIndex —
                // not the variable name. Resolve via base_type_id.
                pushBlockName = compiler.GetName(pc.base_type_id);
                break; // SPIR-V allows at most one push-constant block per stage.
            }

            compiler.glslOptions = new GLSLCompilerOptions
            {
                version = (uint)glslVersion,
                ES = false,
                vulkanSemantics = false,
                emitPushConstantAsUniformBuffer = true,
                enable420PackExtension = true,
                separateShaderObjects = false,
            };
            var source = compiler.Compile();
            return new GlslOutput(source, pushBlockName);
        }
    }

    /// <summary>
    ///     Translate SPIR-V to Metal Shading Language for the macOS Metal backend.
    ///     <paramref name="iosTarget"/> = true emits the iOS variant.
    ///     <para>Returns the source plus the renamed entry-point names. SPIRV-Cross renames
    ///     <c>main</c> to <c>main0</c> in MSL because <c>main</c> is reserved by the C++/MSL
    ///     compiler — the backend needs the renamed name to call <c>NewFunction</c>.</para>
    /// </summary>
    public static MslOutput SpirvToMsl(byte[] spirv, bool iosTarget = false)
    {
        lock (SyncRoot)
        {
            var ctx = GetContext();
            var compiler = ctx.CreateMSLCompiler(spirv);
            compiler.metalOptions = new MSLCompilerOptions
            {
                platform = iosTarget ? Platform.IOS : Platform.MacOS,
                msl_version = (2, 3, 0),
                // [[texture(n)]]/[[sampler(n)]] = GLSL binding n, which is the slot the Metal encoder binds
                // (default numbering follows first use and breaks shaders whose bindings are not used in order)
                enableDecorationBinding = true,
            };

            var source = compiler.Compile();
            // GetCleansedEntryPointName returns the SPIR-V-side name (still 'main') even after
            // SPIRV-Cross's MSL emitter renames the actual function (typically to 'main0') to
            // dodge MSL's reservation of 'main'. The only reliable way to learn the MSL name is
            // to scan the emitted source for the [[vertex]] / [[fragment]] / [[kernel]] function.
            var vertEntry = ExtractMslEntry(source, "vertex");
            var fragEntry = ExtractMslEntry(source, "fragment");
            var compEntry = ExtractMslEntry(source, "kernel");
            return new MslOutput(source, vertEntry, fragEntry, compEntry);
        }
    }

    /// <summary>
    ///     UBO binding slot Penelope shaders should declare for push-constant emulation. The
    ///     OpenGL backend binds the emulating UBO at this slot. Vulkan ignores this — it uses
    ///     the native push-constant path.
    ///     <para>Convention only: SPIRV-Cross preserves whatever set/binding the source shader
    ///     declared on the push-constant block, so for a clean OpenGL fallback shaders should
    ///     follow the convention <c>layout(push_constant) ...</c> without an explicit binding
    ///     (SPIRV-Cross assigns a free slot).</para>
    /// </summary>
    public const int PushConstantBinding = 0;

    /// <summary>
    ///     Compile Vulkan-flavored GLSL 450 to SPIR-V at runtime via glslang. Equivalent to
    ///     running <c>glslc --target-env=vulkan1.3</c> at build time, but doesn't require the
    ///     Vulkan SDK. Useful for tools, hot-reload, and Linux developer setups where glslc isn't
    ///     installed system-wide.
    /// </summary>
    public static byte[] CompileGlslToSpirv(string glsl, ShaderStage stage, string? sourceName = null)
    {
        lock (SyncRoot)
        {
            // Target Vulkan 1.1 / SPIR-V 1.3 deliberately, even though we ultimately serve OpenGL
            // and Vulkan 1.3 callers. Reason: glslang on Vulkan 1.2+ compiles `discard` to
            // OpDemoteToHelperInvocation (SPV_EXT_demote_to_helper_invocation), which SPIRV-Cross
            // refuses to translate when emitting non-Vulkan GLSL — the OpenGL backend would die.
            // SPIR-V 1.3 keeps `discard` as the classic OpKill, which both backends consume cleanly.
            var input = new CompilationInput
            {
                language = SourceType.GLSL,
                stage = ToGlslangStage(stage),
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

            using var shader = new Shader(input);
            if (!shader.Preprocess())
                throw new InvalidOperationException(
                    $"glslang preprocess failed for '{sourceName ?? "shader"}':\n{shader.GetInfoLog()}\n{shader.GetDebugLog()}");
            if (!shader.Parse())
                throw new InvalidOperationException(
                    $"glslang parse failed for '{sourceName ?? "shader"}':\n{shader.GetInfoLog()}\n{shader.GetDebugLog()}");

            using var program = new Program();
            program.AddShader(shader);
            if (!program.Link(MessageType.Default | MessageType.SpvRules | MessageType.VulkanRules))
                throw new InvalidOperationException(
                    $"glslang link failed for '{sourceName ?? "shader"}':\n{program.GetInfoLog()}\n{program.GetDebugLog()}");

            if (!program.GenerateSPIRV(out var words, ToGlslangStage(stage)))
                throw new InvalidOperationException(
                    $"glslang SPIR-V generation failed for '{sourceName ?? "shader"}':\n{program.GetInfoLog()}\n{program.GetDebugLog()}");

            // Convert uint[] words → byte[] (little-endian, matches SPIR-V binary on disk).
            var bytes = new byte[words.Length * 4];
            System.Buffer.BlockCopy(words, 0, bytes, 0, bytes.Length);
            return bytes;
        }
    }

    private static GlslangShaderStage ToGlslangStage(ShaderStage s) => s switch
    {
        ShaderStage.Vertex => GlslangShaderStage.Vertex,
        ShaderStage.Fragment => GlslangShaderStage.Fragment,
        ShaderStage.Compute => GlslangShaderStage.Compute,
        _ => throw new ArgumentException($"Unsupported shader stage for runtime compile: {s}"),
    };

    /// <summary>
    ///     Find the MSL function name annotated with the given stage qualifier
    ///     (<c>vertex</c> / <c>fragment</c> / <c>kernel</c>). MSL declarations look like
    ///     <c>vertex VertOut main0(...)</c> — we return <c>main0</c>.
    /// </summary>
    private static string? ExtractMslEntry(string msl, string stageKeyword)
    {
        // Skip leading whitespace + return type. Match: <stage> <return-type> <name>(
        // Anchored to a word boundary at the start of a line so we don't match inside comments.
        var pattern = new System.Text.RegularExpressions.Regex(
            $@"^\s*{stageKeyword}\s+\S+\s+([A-Za-z_]\w*)\s*\(",
            System.Text.RegularExpressions.RegexOptions.Multiline);
        var m = pattern.Match(msl);
        return m.Success ? m.Groups[1].Value : null;
    }
}

/// <summary>
///     Output of <see cref="ShaderLib.SpirvToGlsl"/>. <see cref="Source"/> is the translated GLSL.
///     <see cref="PushConstantBlockName"/> is the SPIRV-Cross-assigned name of the rewritten push
///     constant block, or null if the shader declared none. Backends use that name to resolve the
///     GL uniform-block index and pin it to <see cref="ShaderLib.PushConstantBinding"/>.
/// </summary>
public readonly record struct GlslOutput(string Source, string? PushConstantBlockName);

/// <summary>
///     Output of <see cref="ShaderLib.SpirvToMsl"/>. <see cref="Source"/> is the translated MSL.
///     <see cref="VertexEntry"/>/<see cref="FragmentEntry"/>/<see cref="ComputeEntry"/> are the
///     names SPIRV-Cross used in the emitted MSL — typically <c>main0</c> instead of <c>main</c>
///     because <c>main</c> is reserved in the C++-derived MSL grammar. Pass these to
///     <c>MTLLibrary.NewFunction</c>.
/// </summary>
public readonly record struct MslOutput(
    string Source,
    string? VertexEntry,
    string? FragmentEntry,
    string? ComputeEntry);
