using System.Numerics;
using System.Runtime.InteropServices;
using Penelope;

namespace Kansei.Graphics;

/// <summary>
///     Draws the text of an <see cref="Overlay"/> (<see cref="Overlay.GlyphVertices"/>) on top of a finished frame, after
///     <see cref="OverlayRenderer"/>: the <see cref="SdfFont"/> atlas as an R8 texture, one render pass that loads the
///     target, one draw. Vertices go to clip space on the CPU, so the pipeline needs no push constants or uniforms.
/// </summary>
public sealed class TextRenderer : IDisposable
{
    private readonly IPenelopeDevice _device;
    private readonly ShaderHandle _shader;
    private readonly RenderPipelineHandle _pipeline;
    private readonly TransientBufferRing _ring;
    private readonly TextureHandle _atlas;
    private readonly BindGroupHandle _group;
    private readonly GlyphVertex[] _clip = new GlyphVertex[Overlay.MaxGlyphVertices];

    public TextRenderer(IPenelopeDevice device, SdfFont font)
    {
        _device = device;
        _atlas = device.CreateTexture(TextureDesc.Sampled2D(font.AtlasWidth, font.AtlasHeight, TextureFormat.R8Unorm, 1, "font"), font.Atlas);
        var layout = device.GetBindGroupLayout(new BindGroupLayoutDesc(
            [new BindGroupLayoutEntry(0, BindingType.CombinedImageSampler, ShaderStage.Fragment)], "text"));
        _group = device.CreateBindGroup(new BindGroupDesc(layout,
            [BindGroupEntry.CombinedImageSampler(0, device.DefaultTextureView(_atlas), device.GetSampler(SamplerDesc.Linear))], "text"));
        _shader = device.CreateShader(ShaderLoader.LoadGraphics(typeof(TextRenderer).Assembly, "text", "text", "text"));
        _pipeline = device.CreateRenderPipeline(new RenderPipelineDesc(
            _shader, GlyphVertex.Layout, PrimitiveTopology.TriangleList, RasterizerState.Default, DepthStencilState.Disabled,
            MultisampleState.Disabled, [new ColorTargetState(device.SwapchainFormat, BlendState.AlphaBlend)], null, [layout], [], "text"));
        _ring = new TransientBufferRing(device, Overlay.MaxGlyphVertices * GlyphVertex.Size, BufferUsage.Vertex, "text");
    }

    public void Draw(ICommandEncoder encoder, Overlay overlay, TextureViewHandle target, int width, int height)
    {
        var glyphs = overlay.GlyphVertices;
        if (glyphs.Length == 0) return;
        // pixel → clip: x right, y down on screen; Vulkan's clip space is Y-down already, Metal/GL Y-up
        var flip = _device.Backend == BackendKind.Vulkan ? 1f : -1f;
        Vector2 scale = new(2f / width, 2f * flip / height), offset = new(-1, -flip);
        for (var i = 0; i < glyphs.Length; i++) _clip[i] = glyphs[i] with { Position = glyphs[i].Position * scale + offset };
        var alloc = _ring.Allocate(glyphs.Length * GlyphVertex.Size, 16);
        MemoryMarshal.AsBytes(_clip.AsSpan(0, glyphs.Length)).CopyTo(alloc.Write);
        using var pass = encoder.BeginRenderPass(new RenderPassDesc(
            [new ColorAttachment(target, LoadOp.Load, StoreOp.Store, ClearColor.Black)], DebugName: "text"));
        pass.SetViewport(0, 0, width, height);
        pass.SetScissor(0, 0, width, height);
        pass.SetPipeline(_pipeline);
        pass.SetBindGroup(0, _group);
        pass.SetVertexBuffer(0, _ring.Buffer, alloc.Offset);
        pass.Draw(glyphs.Length, 1, 0);
    }

    public void Dispose()
    {
        _device.DestroyRenderPipeline(_pipeline);
        _device.DestroyShader(_shader);
        _device.DestroyBindGroup(_group);
        _device.DestroyTexture(_atlas);
        _ring.Dispose();
    }
}
