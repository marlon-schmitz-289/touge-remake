using System.Numerics;
using System.Runtime.InteropServices;
using Penelope;

namespace Kansei.Graphics;

/// <summary>
///     HDR scene texture (single-sample; the MSAA target resolves into it) → bloom (soft threshold, 4×4-box
///     downsample chain to 1/64, tent upsample back) → tonemap/grade/vignette into the output (swapchain format).
/// </summary>
internal sealed class PostProcess : IDisposable
{
    public const TextureFormat HdrFormat = TextureFormat.Rgba16Float;
    private const int BloomLevels = 6; // 1/2 … 1/64

    private readonly IPenelopeDevice _device;
    private readonly ShaderHandle _down, _up, _tonemap;
    private readonly RenderPipelineHandle _downPipeline, _upPipeline, _tonemapPipeline;
    private readonly BindGroupLayoutHandle _oneTex, _twoTex;
    private readonly SamplerHandle _sampler;
    private readonly List<(TextureHandle Tex, TextureViewHandle View, BindGroupHandle Group, int W, int H)> _targets = [];
    private BindGroupHandle _tonemapGroup;
    private int _w, _h;

    public TextureViewHandle SceneView => _targets[0].View;

    public PostProcess(IPenelopeDevice device)
    {
        _device = device;
        var asm = typeof(PostProcess).Assembly;
        _down = device.CreateShader(ShaderLoader.LoadGraphics(asm, "fullscreen", "bloom_down", "bloom_down"));
        _up = device.CreateShader(ShaderLoader.LoadGraphics(asm, "fullscreen", "bloom_up", "bloom_up"));
        _tonemap = device.CreateShader(ShaderLoader.LoadGraphics(asm, "fullscreen", "tonemap", "tonemap"));
        _oneTex = device.GetBindGroupLayout(new BindGroupLayoutDesc(
            [new BindGroupLayoutEntry(0, BindingType.CombinedImageSampler, ShaderStage.Fragment)], "post-1"));
        _twoTex = device.GetBindGroupLayout(new BindGroupLayoutDesc(
        [
            new BindGroupLayoutEntry(0, BindingType.CombinedImageSampler, ShaderStage.Fragment),
            new BindGroupLayoutEntry(1, BindingType.CombinedImageSampler, ShaderStage.Fragment),
        ], "post-2"));
        _sampler = device.GetSampler(SamplerDesc.Linear);
        _downPipeline = Fullscreen(device, _down, HdrFormat, BlendState.Opaque, _oneTex, 32);
        _upPipeline = Fullscreen(device, _up, HdrFormat, BlendState.Additive with { SrcColor = BlendFactor.One }, _oneTex, 32);
        _tonemapPipeline = Fullscreen(device, _tonemap, device.SwapchainFormat, BlendState.Opaque, _twoTex, 48);
    }

    /// <summary>Pipeline for a fragment-only pass over <c>fullscreen.vert</c> (no vertex buffer, no depth).</summary>
    internal static RenderPipelineHandle Fullscreen(IPenelopeDevice device, ShaderHandle shader, TextureFormat format, BlendState blend,
        BindGroupLayoutHandle? layout, int pushBytes, int samples = 1, TextureFormat? depth = null) =>
        device.CreateRenderPipeline(new RenderPipelineDesc(
            shader, new VertexLayout(), PrimitiveTopology.TriangleList, RasterizerState.Default, DepthStencilState.Disabled,
            MultisampleState.Disabled with { SampleCount = samples }, [new ColorTargetState(format, blend)], depth,
            layout is { } l ? [l] : [], [new PushConstantRange(ShaderStage.Fragment, 0, pushBytes)], "fullscreen"));

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
        _tonemapGroup = _device.CreateBindGroup(new BindGroupDesc(_twoTex,
            [BindGroupEntry.CombinedImageSampler(0, _targets[0].View, _sampler), BindGroupEntry.CombinedImageSampler(1, _targets[1].View, _sampler)]));
    }

    public void Run(ICommandEncoder encoder, Atmosphere a, bool bloom, TextureViewHandle output, int outW, int outH)
    {
        Span<byte> push = stackalloc byte[48];
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
        Pass(encoder, output, LoadOp.DontCare, outW, outH, _tonemapPipeline, _tonemapGroup, push);
    }

    private static void Pass(ICommandEncoder encoder, TextureViewHandle target, LoadOp load, int w, int h, RenderPipelineHandle pipeline,
        BindGroupHandle group, ReadOnlySpan<byte> push)
    {
        using var pass = encoder.BeginRenderPass(new RenderPassDesc(
            [new ColorAttachment(target, load, StoreOp.Store, ClearColor.Black)], DebugName: "post"));
        pass.SetViewport(0, 0, w, h);
        pass.SetScissor(0, 0, w, h);
        pass.SetPipeline(pipeline);
        pass.SetBindGroup(0, group);
        pass.SetPushConstants(ShaderStage.Fragment, 0, push);
        pass.Draw(3);
    }

    private void ReleaseTargets()
    {
        if (_targets.Count == 0) return;
        _device.DestroyBindGroup(_tonemapGroup);
        foreach (var (tex, view, group, _, _) in _targets)
        {
            _device.DestroyBindGroup(group);
            _device.DestroyTextureView(view);
            _device.DestroyTexture(tex);
        }
        _targets.Clear();
    }

    public void Dispose()
    {
        ReleaseTargets();
        foreach (var p in new[] { _downPipeline, _upPipeline, _tonemapPipeline }) _device.DestroyRenderPipeline(p);
        foreach (var s in new[] { _down, _up, _tonemap }) _device.DestroyShader(s);
    }
}
