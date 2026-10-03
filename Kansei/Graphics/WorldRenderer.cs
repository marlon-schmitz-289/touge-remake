using System.Numerics;
using System.Runtime.InteropServices;
using Penelope;

namespace Kansei.Graphics;

/// <summary>
///     Forward renderer for prelit static geometry: texture × vertex colour, alpha test, distance fog.
///     No backface culling (the PS2 draws foliage cards from both sides); exact duplicates must be removed by
///     the caller. Reversed-Z (use <see cref="Perspective"/>) with GreaterEqual: overlay layers a few cm apart
///     stay stable at distance, exactly coplanar layers resolve by draw order (later wins), like on the PS2.
///     Owns textures and a depth buffer.
/// </summary>
public sealed class WorldRenderer : IDisposable
{
    private const int PushBytes = 96; // mvp 64 + fog 16 + eye 16
    private static readonly TextureFormat DepthFormat = TextureFormat.Depth32Float;

    private readonly IPenelopeDevice _device;
    private readonly ShaderHandle _shader;
    private readonly BindGroupLayoutHandle _layout;
    private readonly RenderPipelineHandle _pipeline;
    private readonly SamplerHandle _sampler;
    private readonly List<(TextureHandle Tex, TextureViewHandle View, BindGroupHandle Group)> _textures = [];
    private TextureHandle _depth;
    private TextureViewHandle _depthView;
    private int _w, _h;

    public IPenelopeDevice Device => _device;
    public Vector3 FogColor = new(0.62f, 0.72f, 0.85f);
    public float FogDistance = 1200f;

    public WorldRenderer(IPenelopeDevice device)
    {
        _device = device;
        _shader = device.CreateShader(ShaderLoader.LoadGraphics(typeof(WorldRenderer).Assembly, "world", "world", "world"));
        _layout = device.GetBindGroupLayout(new BindGroupLayoutDesc(
            [new BindGroupLayoutEntry(0, BindingType.CombinedImageSampler, ShaderStage.Fragment)], "world-tex"));
        _sampler = device.GetSampler(SamplerDesc.LinearWrap);
        _pipeline = device.CreateRenderPipeline(new RenderPipelineDesc(
            _shader, WorldVertex.Layout, PrimitiveTopology.TriangleList,
            RasterizerState.Default, DepthStencilState.DepthLessWrite with { DepthCompare = CompareFunc.GreaterEqual }, MultisampleState.Disabled,
            [new ColorTargetState(device.SwapchainFormat, BlendState.Opaque)], DepthFormat,
            [_layout], [new PushConstantRange(ShaderStage.Vertex | ShaderStage.Fragment, 0, PushBytes)], "world"));
    }

    /// <summary>
    ///     Right-handed, infinite far plane, reversed-Z (depth 1 at <paramref name="near"/>, 0 at infinity, range 0..1).
    ///     <paramref name="flipY"/> for Y-down clip space (Vulkan).
    /// </summary>
    public static Matrix4x4 Perspective(float fovY, float aspect, float near, bool flipY)
    {
        var f = 1f / MathF.Tan(fovY / 2);
        return new Matrix4x4(
            f / aspect, 0, 0, 0,
            0, flipY ? -f : f, 0, 0,
            0, 0, 0, -1,
            0, 0, near, 0);
    }

    /// <summary>Uploads an RGBA8 texture, returns its index for <see cref="MeshBatch.Texture"/>.</summary>
    public int AddTexture(int width, int height, ReadOnlySpan<byte> rgba, string? name = null)
    {
        var tex = _device.CreateTexture(TextureDesc.Sampled2D(width, height, TextureFormat.Rgba8Unorm, 1, name), rgba);
        var view = _device.DefaultTextureView(tex);
        var group = _device.CreateBindGroup(new BindGroupDesc(_layout, [BindGroupEntry.CombinedImageSampler(0, view, _sampler)], name));
        _textures.Add((tex, view, group));
        return _textures.Count - 1;
    }

    /// <summary>Opens a colour+depth pass on the swapchain, or on <paramref name="target"/> (same format) if given.</summary>
    public IRenderPassEncoder BeginScene(ICommandEncoder encoder, Vector3 clear, FrameCapture? target = null)
    {
        EnsureDepth(target?.Width ?? _device.SwapchainWidth, target?.Height ?? _device.SwapchainHeight);
        var view = target?.View ?? _device.CurrentSwapchainView;
        var pass = encoder.BeginRenderPass(new RenderPassDesc(
            [new ColorAttachment(view, LoadOp.Clear, StoreOp.Store, new ClearColor(clear.X, clear.Y, clear.Z, 1f))],
            new DepthStencilAttachment(_depthView, LoadOp.Clear, StoreOp.Store, 0f, LoadOp.Load, StoreOp.DontCare, 0, false, false),
            DebugName: "world"));
        pass.SetViewport(0, 0, _w, _h);
        return pass;
    }

    public void Draw(IRenderPassEncoder pass, StaticMesh mesh, in Matrix4x4 viewProj, Vector3 eye)
    {
        pass.SetPipeline(_pipeline);
        Span<byte> push = stackalloc byte[PushBytes];
        MemoryMarshal.Write(push, in viewProj);
        MemoryMarshal.Write(push[64..], new Vector4(FogColor, 1f / FogDistance));
        MemoryMarshal.Write(push[80..], new Vector4(eye, 0));
        pass.SetPushConstants(ShaderStage.Vertex | ShaderStage.Fragment, 0, push);
        pass.SetVertexBuffer(0, mesh.Vertices);
        pass.SetIndexBuffer(mesh.Indices, IndexType.UInt32);
        foreach (var b in mesh.Batches)
        {
            pass.SetBindGroup(0, _textures[b.Texture].Group);
            pass.DrawIndexed(b.IndexCount, 1, b.FirstIndex);
        }
    }

    private void EnsureDepth(int w, int h)
    {
        if (w == _w && h == _h && !_depthView.IsNull) return;
        if (!_depthView.IsNull) _device.DestroyTextureView(_depthView);
        if (!_depth.IsNull) _device.DestroyTexture(_depth);
        _depth = _device.CreateTexture(TextureDesc.DepthAttachment(w, h, DepthFormat, "world-depth"));
        _depthView = _device.DefaultTextureView(_depth);
        (_w, _h) = (w, h);
    }

    public void Dispose()
    {
        foreach (var (tex, view, group) in _textures)
        {
            _device.DestroyBindGroup(group);
            _device.DestroyTextureView(view);
            _device.DestroyTexture(tex);
        }
        if (!_depthView.IsNull) _device.DestroyTextureView(_depthView);
        if (!_depth.IsNull) _device.DestroyTexture(_depth);
        _device.DestroyRenderPipeline(_pipeline);
        _device.DestroyShader(_shader);
    }
}
