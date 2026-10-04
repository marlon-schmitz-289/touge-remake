using System.Numerics;
using System.Runtime.InteropServices;
using Penelope;

namespace Kansei.Graphics;

/// <summary>
///     Single-sample scene targets the MSAA pass resolves into (HDR colour, <see cref="GbufFormat"/> gbuffer, depth) →
///     half-res ambient occlusion (ao.frag + 4×4 bilateral blur) and wet-ground reflections (ssr.frag + vertical streak
///     blur ssr_blur.frag) → bloom (soft
///     threshold, 4×4-box downsample chain to 1/64, tent upsample back) → tonemap (AO on the ambient share, SSR,
///     grade, vignette, dither) into the output (swapchain format). AO, SSR and bloom each switchable.
/// </summary>
internal sealed class PostProcess : IDisposable
{
    public const TextureFormat HdrFormat = TextureFormat.Rgba16Float;
    /// <summary>Second scene target: r = ambient share of the colour (AO darkens only that), g = reflection weight (SSR), b = streak (SSR blur).</summary>
    public const TextureFormat GbufFormat = TextureFormat.Rgba8Unorm;
    public const TextureFormat DepthFormat = TextureFormat.Depth32Float;
    private const int BloomLevels = 6; // 1/2 … 1/64
    /// <summary>AO search radius (m) and strength (ao.frag).</summary>
    private const float AoRadius = 1.2f, AoStrength = 1.4f;
    /// <summary>Half length of the reflection streak on rough wet asphalt in the screen (share of its height, ssr_blur.frag).</summary>
    private const float SsrStreak = 0.04f;

    private readonly IPenelopeDevice _device;
    private readonly ShaderHandle[] _shaders;
    private readonly RenderPipelineHandle _downPipeline, _upPipeline, _tonemapPipeline, _aoPipeline, _blurPipeline, _ssrPipeline, _ssrBlurPipeline;
    private readonly BindGroupLayoutHandle _oneTex, _twoTex, _ssrLayout, _sixTex;
    /// <summary>
    ///     Blocks too big for Vulkan's guaranteed 128 bytes of push constants (scene_push.glsl, sky, SSR): one
    ///     <see cref="UniformBytes"/> slice per draw, bound with a dynamic offset (<see cref="Upload"/>).
    /// </summary>
    private readonly TransientBufferRing _uniforms;
    private readonly int _uniformAlign;
    /// <summary>Slice size: the largest block (scene_push.glsl, 720 bytes) rounded up to the 256-byte offset alignment.</summary>
    public const int UniformBytes = 768;
    /// <summary>Binding of the uniform slice in its bind group (scene group: 6, SSR: 3); Metal flattens bindings to buffer indices, 0 is the push block.</summary>
    public static BindGroupLayoutEntry UniformEntry(int binding, ShaderStage stages) =>
        new(binding, BindingType.UniformBuffer, stages, UniformBytes, HasDynamicOffset: true);
    public BufferHandle UniformBuffer => _uniforms.Buffer;
    private readonly SamplerHandle _sampler, _point;
    private readonly List<(TextureHandle Tex, TextureViewHandle View, BindGroupHandle Group, int W, int H)> _targets = [];
    private (TextureHandle Tex, TextureViewHandle View) _gbuf, _depth, _ao, _aoBlur, _ssr, _ssrBlur;
    private BindGroupHandle _tonemapGroup, _aoGroup, _blurGroup, _ssrGroup, _ssrBlurGroup;
    private int _w, _h;

    public TextureViewHandle SceneView => _targets[0].View;
    public TextureViewHandle GbufView => _gbuf.View;
    /// <summary>Single-sample scene depth: the MSAA depth resolves into it, or the 1-sample pass renders into it directly.</summary>
    public TextureViewHandle DepthView => _depth.View;

    public PostProcess(IPenelopeDevice device)
    {
        _device = device;
        var asm = typeof(PostProcess).Assembly;
        _shaders = [.. new[] { "bloom_down", "bloom_up", "tonemap", "ao", "ao_blur", "ssr", "ssr_blur" }.Select(f => device.CreateShader(ShaderLoader.LoadGraphics(asm, "fullscreen", f, f)))];
        BindGroupLayoutHandle Layout(int n) => device.GetBindGroupLayout(new BindGroupLayoutDesc(
            [.. Enumerable.Range(0, n).Select(b => new BindGroupLayoutEntry(b, BindingType.CombinedImageSampler, ShaderStage.Fragment))], $"post-{n}"));
        (_oneTex, _twoTex, _sixTex) = (Layout(1), Layout(2), Layout(6));
        _ssrLayout = device.GetBindGroupLayout(new BindGroupLayoutDesc(
            [.. Enumerable.Range(0, 3).Select(b => new BindGroupLayoutEntry(b, BindingType.CombinedImageSampler, ShaderStage.Fragment)),
                UniformEntry(3, ShaderStage.Fragment)], "post-ssr"));
        _uniformAlign = Math.Max(256, device.Limits.MinUniformBufferOffsetAlignment);
        // ponytail: fixed 1 MB per frame (~1300 draws); grow if the scene ever draws more batches with own blocks
        _uniforms = new TransientBufferRing(device, 1 << 20, BufferUsage.Uniform, "uniform-slices");
        _sampler = device.GetSampler(SamplerDesc.Linear);
        _point = device.GetSampler(SamplerDesc.Nearest);
        _downPipeline = Fullscreen(device, _shaders[0], HdrFormat, BlendState.Opaque, [_oneTex], 32);
        _upPipeline = Fullscreen(device, _shaders[1], HdrFormat, BlendState.Additive with { SrcColor = BlendFactor.One }, [_oneTex], 32);
        _tonemapPipeline = Fullscreen(device, _shaders[2], device.SwapchainFormat, BlendState.Opaque, [_sixTex], 64);
        _aoPipeline = Fullscreen(device, _shaders[3], TextureFormat.Rg16Float, BlendState.Opaque, [_oneTex], 32);
        _blurPipeline = Fullscreen(device, _shaders[4], TextureFormat.Rg16Float, BlendState.Opaque, [_oneTex], 0);
        _ssrPipeline = Fullscreen(device, _shaders[5], HdrFormat, BlendState.Opaque, [_ssrLayout], 0);
        _ssrBlurPipeline = Fullscreen(device, _shaders[6], HdrFormat, BlendState.Opaque, [_twoTex], 16);
    }

    /// <summary>
    ///     Pipeline for a fragment-only pass over <c>fullscreen.vert</c> (no vertex buffer, no depth test).
    ///     <paramref name="gbuf"/>: also the scene pass's gbuffer target (the sky).
    /// </summary>
    internal static RenderPipelineHandle Fullscreen(IPenelopeDevice device, ShaderHandle shader, TextureFormat format, BlendState blend,
        BindGroupLayoutHandle[] layouts, int pushBytes, int samples = 1, TextureFormat? depth = null, bool gbuf = false) =>
        device.CreateRenderPipeline(new RenderPipelineDesc(
            shader, new VertexLayout(), PrimitiveTopology.TriangleList, RasterizerState.Default, DepthStencilState.Disabled,
            MultisampleState.Disabled with { SampleCount = samples },
            gbuf ? [new ColorTargetState(format, blend), new ColorTargetState(GbufFormat, blend)] : [new ColorTargetState(format, blend)], depth,
            layouts, pushBytes > 0 ? [new PushConstantRange(ShaderStage.Fragment, 0, pushBytes)] : [], "fullscreen"));

    public void Resize(int w, int h)
    {
        if (w == _w && h == _h) return;
        ReleaseTargets();
        (_w, _h) = (w, h);
        for (var i = 0; i <= BloomLevels; i++)
        {
            int lw = Math.Max(1, w >> i), lh = Math.Max(1, h >> i);
            var tex = _device.CreateTexture(TextureDesc.ColorAttachment(lw, lh, HdrFormat, i == 0 ? "hdr-scene" : $"bloom{i}"));
            var view = _device.DefaultTextureView(tex);
            var group = _device.CreateBindGroup(new BindGroupDesc(_oneTex, [BindGroupEntry.CombinedImageSampler(0, view, _sampler)]));
            _targets.Add((tex, view, group, lw, lh));
        }
        (int W, int H) half = (Math.Max(1, w / 2), Math.Max(1, h / 2));
        _gbuf = Target(w, h, GbufFormat, TextureUsage.ColorAttachment, "gbuffer");
        _depth = Target(w, h, DepthFormat, TextureUsage.DepthStencilAttachment, "scene-depth-resolved");
        _ao = Target(half.W, half.H, TextureFormat.Rg16Float, TextureUsage.ColorAttachment, "ao");
        _aoBlur = Target(half.W, half.H, TextureFormat.Rg16Float, TextureUsage.ColorAttachment, "ao-blur");
        _ssr = Target(half.W, half.H, HdrFormat, TextureUsage.ColorAttachment, "ssr");
        _ssrBlur = Target(half.W, half.H, HdrFormat, TextureUsage.ColorAttachment, "ssr-blur");
        _aoGroup = Group(_oneTex, (_depth.View, _point));
        _blurGroup = Group(_oneTex, (_ao.View, _point));
        _ssrGroup = _device.CreateBindGroup(new BindGroupDesc(_ssrLayout,
        [
            BindGroupEntry.CombinedImageSampler(0, SceneView, _sampler), BindGroupEntry.CombinedImageSampler(1, _depth.View, _point),
            BindGroupEntry.CombinedImageSampler(2, _gbuf.View, _point), BindGroupEntry.UniformBuffer(3, UniformBuffer, 0, UniformBytes),
        ]));
        _ssrBlurGroup = Group(_twoTex, (_ssr.View, _sampler), (_gbuf.View, _point));
        _tonemapGroup = Group(_sixTex, (SceneView, _sampler), (_targets[1].View, _sampler), (_aoBlur.View, _point), (_ssrBlur.View, _sampler),
            (_gbuf.View, _point), (_depth.View, _point));
    }

    private (TextureHandle, TextureViewHandle) Target(int w, int h, TextureFormat format, TextureUsage usage, string name)
    {
        var tex = _device.CreateTexture(new TextureDesc(w, h, format, usage | TextureUsage.Sampled, DebugName: name));
        return (tex, _device.DefaultTextureView(tex));
    }

    /// <summary>Copies <paramref name="data"/> into this frame's next uniform slice, returns its dynamic offset.</summary>
    public int Upload(ReadOnlySpan<byte> data)
    {
        var a = _uniforms.Allocate(UniformBytes, _uniformAlign);
        data.CopyTo(a.Write);
        return a.Offset;
    }

    private BindGroupHandle Group(BindGroupLayoutHandle layout, params (TextureViewHandle View, SamplerHandle Sampler)[] textures) =>
        _device.CreateBindGroup(new BindGroupDesc(layout, [.. textures.Select((t, i) => BindGroupEntry.CombinedImageSampler(i, t.View, t.Sampler))]));

    /// <summary>
    ///     <paramref name="ao"/>, <paramref name="bloom"/>, <paramref name="reflections"/> (SSR, wet only) per pass; the output may differ
    ///     in size from the scene (render scale). <paramref name="viewRotProj"/> = view rotation (no translation) ×
    ///     <paramref name="proj"/> (<see cref="WorldRenderer.Perspective"/>), <paramref name="ySign"/> = clip y per uv y.
    /// </summary>
    public void Run(ICommandEncoder encoder, Atmosphere a, bool ao, bool bloom, bool reflections, TextureViewHandle output, int outW, int outH, in Matrix4x4 viewRotProj,
        in Matrix4x4 proj, float ySign)
    {
        Span<byte> push = stackalloc byte[144];
        var near = proj.M43;
        int hw = Math.Max(1, _w / 2), hh = Math.Max(1, _h / 2);
        var ssr = reflections && a.Wetness > 0;
        if (ao)
        {
            MemoryMarshal.Write(push, new Vector4(1 / proj.M11, 1 / MathF.Abs(proj.M22), near, AoRadius));
            MemoryMarshal.Write(push[16..], new Vector4(1f / _w, 1f / _h, AoStrength, 0));
            Pass(encoder, _ao.View, LoadOp.DontCare, hw, hh, _aoPipeline, _aoGroup, push[..32]);
            Pass(encoder, _aoBlur.View, LoadOp.DontCare, hw, hh, _blurPipeline, _blurGroup, []);
        }
        if (ssr)
        {
            Matrix4x4.Invert(viewRotProj, out var inv);
            MemoryMarshal.Write(push, in viewRotProj);
            MemoryMarshal.Write(push[64..], in inv);
            MemoryMarshal.Write(push[128..], new Vector4(near, ySign, 1f / _w, 1f / _h));
            Pass(encoder, _ssr.View, LoadOp.DontCare, hw, hh, _ssrPipeline, _ssrGroup, [], Upload(push));
            MemoryMarshal.Write(push, new Vector4(1f / hw, 1f / hh, SsrStreak * hh, 0));
            Pass(encoder, _ssrBlur.View, LoadOp.DontCare, hw, hh, _ssrBlurPipeline, _ssrBlurGroup, push[..16]);
        }
        if (bloom)
        {
            for (var i = 1; i <= BloomLevels; i++)
            {
                var (src, dst) = (_targets[i - 1], _targets[i]);
                MemoryMarshal.Write(push, new Vector4(1f / src.W, 1f / src.H, i == 1 ? a.BloomThreshold : 0, a.BloomThreshold * 0.5f));
                MemoryMarshal.Write(push[16..], new Vector4(1f / dst.W, 1f / dst.H, 0, 0));
                Pass(encoder, dst.View, LoadOp.Clear, dst.W, dst.H, _downPipeline, src.Group, push[..32]);
            }
            for (var i = BloomLevels; i > 1; i--)
            {
                var (src, dst) = (_targets[i], _targets[i - 1]);
                MemoryMarshal.Write(push, new Vector4(1f / src.W, 1f / src.H, 0, 0));
                MemoryMarshal.Write(push[16..], new Vector4(1f / dst.W, 1f / dst.H, 0, 0));
                Pass(encoder, dst.View, LoadOp.Load, dst.W, dst.H, _upPipeline, src.Group, push[..32]);
            }
        }
        MemoryMarshal.Write(push, new Vector4(1f / outW, 1f / outH, a.Exposure, bloom ? a.BloomStrength / BloomLevels : 0));
        MemoryMarshal.Write(push[16..], new Vector4(a.Tint, a.Saturation));
        MemoryMarshal.Write(push[32..], new Vector4(a.Vignette, (float)outW / outH, a.Contrast, 0));
        MemoryMarshal.Write(push[48..], new Vector4(ao ? 1 : 0, near, ssr ? 1 : 0, 1));
        Pass(encoder, output, LoadOp.DontCare, outW, outH, _tonemapPipeline, _tonemapGroup, push[..64]);
    }

    private static void Pass(ICommandEncoder encoder, TextureViewHandle target, LoadOp load, int w, int h, RenderPipelineHandle pipeline,
        BindGroupHandle group, ReadOnlySpan<byte> push, int uniformOffset = -1)
    {
        using var pass = encoder.BeginRenderPass(new RenderPassDesc(
            [new ColorAttachment(target, load, StoreOp.Store, ClearColor.Black)], DebugName: "post"));
        pass.SetViewport(0, 0, w, h);
        pass.SetScissor(0, 0, w, h);
        pass.SetPipeline(pipeline);
        if (uniformOffset < 0) pass.SetBindGroup(0, group);
        else pass.SetBindGroup(0, group, [uniformOffset]);
        if (push.Length > 0) pass.SetPushConstants(ShaderStage.Fragment, 0, push);
        pass.Draw(3);
    }

    private void ReleaseTargets()
    {
        if (_targets.Count == 0) return;
        foreach (var g in new[] { _tonemapGroup, _aoGroup, _blurGroup, _ssrGroup, _ssrBlurGroup }) _device.DestroyBindGroup(g);
        foreach (var (tex, view, group, _, _) in _targets)
        {
            _device.DestroyBindGroup(group);
            _device.DestroyTextureView(view);
            _device.DestroyTexture(tex);
        }
        foreach (var (tex, view) in new[] { _gbuf, _depth, _ao, _aoBlur, _ssr, _ssrBlur })
        {
            _device.DestroyTextureView(view);
            _device.DestroyTexture(tex);
        }
        _targets.Clear();
    }

    public void Dispose()
    {
        ReleaseTargets();
        foreach (var p in new[] { _downPipeline, _upPipeline, _tonemapPipeline, _aoPipeline, _blurPipeline, _ssrPipeline, _ssrBlurPipeline }) _device.DestroyRenderPipeline(p);
        foreach (var s in _shaders) _device.DestroyShader(s);
        _uniforms.Dispose();
    }
}
