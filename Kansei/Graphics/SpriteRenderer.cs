using System.Numerics;
using System.Runtime.InteropServices;
using Penelope;

namespace Kansei.Graphics;

[StructLayout(LayoutKind.Sequential)]
public struct SpriteVertex(Vector2 position, Vector2 uv, uint color)
{
    public Vector2 Position = position;
    public Vector2 Uv = uv;
    public uint Color = color;

    public const int Size = 20;

    public static readonly VertexLayout Layout = VertexLayout.Interleaved(Size,
        new VertexAttribute(0, VertexFormat.Float2, 0),
        new VertexAttribute(1, VertexFormat.Float2, 8),
        new VertexAttribute(2, VertexFormat.UByte4Norm, 16));
}

/// <summary>
///     Pictures on top of a finished frame, below the <see cref="Overlay"/> (story manga panels and portraits): RGBA8 images
///     uploaded once (<see cref="Upload"/>, premultiplied, sRGB values passed through like the overlay's colours), drawn as
///     screen-pixel rectangles tinted by an sRGB colour whose alpha fades them. Texture 0 is plain white (solid fills).
///     Queued draws go out with the next <see cref="Draw"/>, in order, one draw call per run of the same texture.
/// </summary>
public sealed class SpriteRenderer : IDisposable
{
    private const int MaxVertices = 6 * 1024;
    private readonly IPenelopeDevice _device;
    private readonly ShaderHandle _shader;
    private readonly RenderPipelineHandle _pipeline;
    private readonly BindGroupLayoutHandle _layout;
    private readonly TransientBufferRing _ring;
    private readonly Dictionary<int, (TextureHandle Texture, BindGroupHandle Group)> _textures = [];
    private readonly SpriteVertex[] _vertices = new SpriteVertex[MaxVertices];
    private readonly List<(int Texture, int First)> _runs = [];
    private int _count, _next;

    public SpriteRenderer(IPenelopeDevice device)
    {
        _device = device;
        _layout = device.GetBindGroupLayout(new BindGroupLayoutDesc(
            [new BindGroupLayoutEntry(0, BindingType.CombinedImageSampler, ShaderStage.Fragment)], "sprite"));
        _shader = device.CreateShader(ShaderLoader.LoadGraphics(typeof(SpriteRenderer).Assembly, "sprite", "sprite", "sprite"));
        _pipeline = device.CreateRenderPipeline(new RenderPipelineDesc(
            _shader, SpriteVertex.Layout, PrimitiveTopology.TriangleList, RasterizerState.Default, DepthStencilState.Disabled,
            MultisampleState.Disabled, [new ColorTargetState(device.SwapchainFormat, BlendState.Premultiplied)], null, [_layout], [], "sprite"));
        _ring = new TransientBufferRing(device, MaxVertices * SpriteVertex.Size, BufferUsage.Vertex, "sprite");
        Upload(1, 1, [255, 255, 255, 255], "white");
    }

    /// <summary>A picture (straight alpha RGBA8, rows top-down) as a new texture; returns its id for <see cref="Add"/>.</summary>
    public int Upload(int width, int height, byte[] rgba, string name)
    {
        var pre = new byte[rgba.Length];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            var a = rgba[i + 3];
            (pre[i], pre[i + 1], pre[i + 2], pre[i + 3]) = ((byte)(rgba[i] * a / 255), (byte)(rgba[i + 1] * a / 255), (byte)(rgba[i + 2] * a / 255), a);
        }
        var tex = _device.CreateTexture(TextureDesc.Sampled2D(width, height, TextureFormat.Rgba8Unorm, 1, name), pre);
        var group = _device.CreateBindGroup(new BindGroupDesc(_layout,
            [BindGroupEntry.CombinedImageSampler(0, _device.DefaultTextureView(tex), _device.GetSampler(SamplerDesc.Linear))], name));
        _textures[_next] = (tex, group);
        return _next++;
    }

    /// <summary>Frees pictures (waits for the GPU first: they may still be in flight).</summary>
    public void Free(IEnumerable<int> ids)
    {
        var list = ids.Where(id => id != 0 && _textures.ContainsKey(id)).ToList();
        if (list.Count == 0) return;
        _device.WaitIdle();
        foreach (var id in list)
        {
            var (tex, group) = _textures[id];
            _device.DestroyBindGroup(group);
            _device.DestroyTexture(tex);
            _textures.Remove(id);
        }
    }

    /// <summary>Picture <paramref name="texture"/> (its <paramref name="uv0"/>…<paramref name="uv1"/> part) into the screen rectangle <paramref name="min"/>…<paramref name="max"/>.</summary>
    public void Add(int texture, Vector2 min, Vector2 max, Vector2 uv0, Vector2 uv1, uint color = 0xFFFFFFFF)
    {
        if (_count + 6 > MaxVertices || !_textures.ContainsKey(texture)) return;
        if (_runs.Count == 0 || _runs[^1].Texture != texture) _runs.Add((texture, _count));
        SpriteVertex a = new(min, uv0, color), b = new(new(max.X, min.Y), new(uv1.X, uv0.Y), color),
            c = new(max, uv1, color), d = new(new(min.X, max.Y), new(uv0.X, uv1.Y), color);
        _vertices[_count++] = a;
        _vertices[_count++] = b;
        _vertices[_count++] = c;
        _vertices[_count++] = a;
        _vertices[_count++] = c;
        _vertices[_count++] = d;
    }

    /// <summary>A solid rectangle (texture 0).</summary>
    public void Fill(Vector2 min, Vector2 max, uint color) => Add(0, min, max, Vector2.Zero, Vector2.One, color);

    public void Draw(ICommandEncoder encoder, TextureViewHandle target, int width, int height)
    {
        if (_count == 0) return;
        // pixel → clip: x right, y down on screen; Vulkan's clip space is Y-down already, Metal/GL Y-up
        var flip = _device.Backend == BackendKind.Vulkan ? 1f : -1f;
        Vector2 scale = new(2f / width, 2f * flip / height), offset = new(-1, -flip);
        for (var i = 0; i < _count; i++) _vertices[i].Position = _vertices[i].Position * scale + offset;
        var alloc = _ring.Allocate(_count * SpriteVertex.Size, 16);
        MemoryMarshal.AsBytes(_vertices.AsSpan(0, _count)).CopyTo(alloc.Write);
        using (var pass = encoder.BeginRenderPass(new RenderPassDesc(
                   [new ColorAttachment(target, LoadOp.Load, StoreOp.Store, ClearColor.Black)], DebugName: "sprite")))
        {
            pass.SetViewport(0, 0, width, height);
            pass.SetScissor(0, 0, width, height);
            pass.SetPipeline(_pipeline);
            pass.SetVertexBuffer(0, _ring.Buffer, alloc.Offset);
            for (var r = 0; r < _runs.Count; r++)
            {
                var end = r + 1 < _runs.Count ? _runs[r + 1].First : _count;
                pass.SetBindGroup(0, _textures[_runs[r].Texture].Group);
                pass.Draw(end - _runs[r].First, 1, _runs[r].First);
            }
        }
        _count = 0;
        _runs.Clear();
    }

    public void Dispose()
    {
        Free(_textures.Keys.ToList());
        var (tex, group) = _textures[0];
        _device.DestroyBindGroup(group);
        _device.DestroyTexture(tex);
        _device.DestroyRenderPipeline(_pipeline);
        _device.DestroyShader(_shader);
        _ring.Dispose();
    }
}
