using System.Numerics;
using System.Runtime.InteropServices;
using Penelope;

namespace Kansei.Graphics;

/// <summary>
///     Draws an <see cref="Overlay"/> alpha-blended on top of a finished frame (after <see cref="WorldRenderer.EndScene"/>,
///     into the swapchain or a <see cref="FrameCapture"/>): one render pass that loads the target, one per-frame vertex
///     ring, one draw per clip batch.
/// </summary>
public sealed class OverlayRenderer : IDisposable
{
    private const int PushBytes = 32;
    private readonly IPenelopeDevice _device;
    private readonly ShaderHandle _shader;
    private readonly RenderPipelineHandle _pipeline;
    private readonly TransientBufferRing _ring;

    public OverlayRenderer(IPenelopeDevice device)
    {
        _device = device;
        _shader = device.CreateShader(ShaderLoader.LoadGraphics(typeof(OverlayRenderer).Assembly, "overlay", "overlay", "overlay"));
        _pipeline = device.CreateRenderPipeline(new RenderPipelineDesc(
            _shader, OverlayVertex.Layout, PrimitiveTopology.TriangleList, RasterizerState.Default, DepthStencilState.Disabled,
            MultisampleState.Disabled, [new ColorTargetState(device.SwapchainFormat, BlendState.AlphaBlend)], null,
            [], [new PushConstantRange(ShaderStage.Vertex | ShaderStage.Fragment, 0, PushBytes)], "overlay"));
        _ring = new TransientBufferRing(device, Overlay.MaxVertices * OverlayVertex.Size, BufferUsage.Vertex, "overlay");
    }

    public void Draw(ICommandEncoder encoder, Overlay overlay, TextureViewHandle target, int width, int height)
    {
        var count = overlay.VertexCount;
        if (count == 0) return;
        var alloc = _ring.Allocate(count * OverlayVertex.Size, 16);
        MemoryMarshal.AsBytes(overlay.Vertices).CopyTo(alloc.Write);
        using var pass = encoder.BeginRenderPass(new RenderPassDesc(
            [new ColorAttachment(target, LoadOp.Load, StoreOp.Store, ClearColor.Black)], DebugName: "overlay"));
        pass.SetViewport(0, 0, width, height);
        pass.SetScissor(0, 0, width, height);
        pass.SetPipeline(_pipeline);
        pass.SetVertexBuffer(0, _ring.Buffer, alloc.Offset);
        Span<byte> push = stackalloc byte[PushBytes];
        // pixel → clip: x right, y down on screen; Vulkan's clip space is Y-down already, Metal/GL Y-up
        MemoryMarshal.Write(push, new Vector4(2f / width, _device.Backend == BackendKind.Vulkan ? 2f / height : -2f / height, 0, 0));
        var batches = overlay.Batches;
        for (var b = 0; b < batches.Length; b++)
        {
            var (first, clip) = batches[b];
            var end = b + 1 < batches.Length ? batches[b + 1].First : count;
            if (end == first) continue;
            MemoryMarshal.Write(push[16..], new Vector4(clip, 0));
            pass.SetPushConstants(ShaderStage.Vertex | ShaderStage.Fragment, 0, push);
            pass.Draw(end - first, 1, first);
        }
    }

    public void Dispose()
    {
        _device.DestroyRenderPipeline(_pipeline);
        _device.DestroyShader(_shader);
        _ring.Dispose();
    }
}
