using System.Numerics;
using System.Runtime.InteropServices;
using Penelope;

namespace Kansei.Graphics;

/// <summary>
///     Sun shadows: <see cref="Cascades"/> cascades around the camera in one depth atlas (tiles side by side),
///     each an orthographic box around the bounding sphere of its slice of the view frustum. The sphere radius only
///     depends on the slice and the field of view (rounded up to 2 m), and the box centre snaps to whole texels in
///     light space, so moving the camera does not make shadow edges crawl. Casters are drawn without culling
///     (two-sided foliage, alpha-tested) with slope-scaled depth bias; receivers add a normal offset of
///     ~1.5 texels (<see cref="TexelWorld"/>) and filter with 3×3 bilinear compare taps (lighting.glsl).
/// </summary>
public sealed class ShadowMap : IDisposable
{
    public const int Cascades = 3, TileSize = 2048;
    /// <summary>Slice ends in metres from the camera; shadows end at the last one (the baked light takes over).</summary>
    private static readonly float[] Splits = [0.3f, 12, 40, 150];
    /// <summary>How far towards the sun casters are collected in front of a cascade's sphere.</summary>
    private const float CasterReach = 250;

    private readonly IPenelopeDevice _device;
    private readonly ShaderHandle _shader;
    private readonly RenderPipelineHandle _worldPipeline, _carPipeline;
    private readonly TextureHandle _atlas;

    /// <summary>World → clip of each cascade (for drawing casters).</summary>
    public readonly Matrix4x4[] ViewProj = new Matrix4x4[Cascades];
    /// <summary>World → tile uv (xy, 0..1 inside the cascade's tile) + depth (z), what receivers sample with.</summary>
    public readonly Matrix4x4[] Lookup = new Matrix4x4[Cascades];
    /// <summary>World size of one shadow texel per cascade.</summary>
    public readonly float[] TexelWorld = new float[Cascades];
    public TextureViewHandle View { get; }
    public SamplerHandle Sampler { get; }

    public ShadowMap(IPenelopeDevice device, BindGroupLayoutHandle textureLayout)
    {
        _device = device;
        _shader = device.CreateShader(ShaderLoader.LoadGraphics(typeof(ShadowMap).Assembly, "shadow", "shadow", "shadow"));
        _atlas = device.CreateTexture(new TextureDesc(TileSize * Cascades, TileSize, WorldRenderer.ShadowFormat,
            TextureUsage.DepthStencilAttachment | TextureUsage.Sampled, DebugName: "shadow-atlas"));
        View = device.DefaultTextureView(_atlas);
        Sampler = device.GetSampler(SamplerDesc.Linear with { Compare = CompareFunc.LessEqual, LodMaxClamp = 0 });
        _worldPipeline = Pipeline(WorldVertex.Size, 12, textureLayout);
        _carPipeline = Pipeline(CarVertex.Size, 24, textureLayout);
    }

    private RenderPipelineHandle Pipeline(int stride, int uvOffset, BindGroupLayoutHandle textureLayout) =>
        _device.CreateRenderPipeline(new RenderPipelineDesc(_shader,
            VertexLayout.Interleaved(stride, new VertexAttribute(0, VertexFormat.Float3, 0), new VertexAttribute(1, VertexFormat.Float2, uvOffset)),
            PrimitiveTopology.TriangleList, RasterizerState.Default,
            DepthStencilState.DepthLessWrite with { DepthBiasConstant = 2, DepthBiasSlope = 2.5f, DepthBiasClamp = 0.01f },
            MultisampleState.Disabled, [], WorldRenderer.ShadowFormat, [textureLayout],
            [new PushConstantRange(ShaderStage.Vertex | ShaderStage.Fragment, 0, 80)], "shadow"));

    /// <summary>
    ///     Fits the cascades to the camera (<paramref name="eye"/>, <paramref name="forward"/>, vertical
    ///     <paramref name="fovY"/>, <paramref name="aspect"/>) for a sun in direction <paramref name="toSun"/>.
    ///     <paramref name="flipY"/>: render targets have v = 0 at clip y = −1 (Vulkan) instead of +1.
    /// </summary>
    public void Update(Vector3 eye, Vector3 forward, float fovY, float aspect, Vector3 toSun, bool flipY)
    {
        var tanY = MathF.Tan(fovY / 2);
        var k2 = tanY * tanY * (1 + aspect * aspect); // squared half-diagonal per metre of depth
        var toTile = new Matrix4x4(0.5f, 0, 0, 0, 0, flipY ? 0.5f : -0.5f, 0, 0, 0, 0, 1, 0, 0.5f, 0.5f, 0, 1);
        for (var c = 0; c < Cascades; c++)
        {
            float n = Splits[c], f = Splits[c + 1];
            // smallest sphere around the slice: centre on the view axis, clamped to the far plane
            var z = MathF.Min((n + f) / 2 * (1 + k2), f);
            var radius = MathF.Ceiling(MathF.Sqrt((f - z) * (f - z) + f * f * k2) / 2) * 2;
            ViewProj[c] = Fit(eye + forward * z, radius, toSun, TileSize, CasterReach);
            Lookup[c] = ViewProj[c] * toTile;
            TexelWorld[c] = 2 * radius / TileSize;
        }
    }

    /// <summary>
    ///     Orthographic light view-projection around a sphere, its centre snapped to whole texels in light space:
    ///     two spheres whose centres differ by less than a texel produce texel-aligned, identical rasterisation.
    /// </summary>
    public static Matrix4x4 Fit(Vector3 centre, float radius, Vector3 toSun, int resolution, float casterReach)
    {
        var up = MathF.Abs(toSun.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
        var view = Matrix4x4.CreateLookAt(Vector3.Zero, -toSun, up);
        var c = Vector3.Transform(centre, view);
        var texel = 2 * radius / resolution;
        c.X = MathF.Floor(c.X / texel) * texel;
        c.Y = MathF.Floor(c.Y / texel) * texel;
        // view space looks down −z: the box spans depths −c.z − reach … −c.z + radius
        return view * Matrix4x4.CreateOrthographicOffCenter(c.X - radius, c.X + radius, c.Y - radius, c.Y + radius,
            -c.Z - casterReach, -c.Z + radius);
    }

    /// <summary>Renders the casters into every cascade tile; <paramref name="cars"/> use <see cref="CarVertex"/>.</summary>
    public void Render(ICommandEncoder encoder, Func<int, BindGroupHandle> textureGroup, StaticMesh world, ReadOnlySpan<(StaticMesh Mesh, Matrix4x4 Model)> cars)
    {
        using var pass = encoder.BeginRenderPass(new RenderPassDesc([],
            new DepthStencilAttachment(View, LoadOp.Clear, StoreOp.Store, 1f, LoadOp.Clear, StoreOp.DontCare, 0, false, false),
            DebugName: "shadows"));
        Span<byte> push = stackalloc byte[80];
        for (var c = 0; c < Cascades; c++)
        {
            pass.SetViewport(c * TileSize, 0, TileSize, TileSize);
            pass.SetScissor(c * TileSize, 0, TileSize, TileSize);
            pass.SetPipeline(_worldPipeline);
            Draw(pass, world, ViewProj[c], 0.3f, textureGroup, push);
            pass.SetPipeline(_carPipeline);
            foreach (var (mesh, model) in cars) Draw(pass, mesh, model * ViewProj[c], 0.5f, textureGroup, push);
        }
    }

    private static void Draw(IRenderPassEncoder pass, StaticMesh mesh, in Matrix4x4 mvp, float cutoff, Func<int, BindGroupHandle> textureGroup, Span<byte> push)
    {
        MemoryMarshal.Write(push, in mvp);
        MemoryMarshal.Write(push[64..], new Vector4(cutoff, 0, 0, 0));
        pass.SetPushConstants(ShaderStage.Vertex | ShaderStage.Fragment, 0, push);
        pass.SetVertexBuffer(0, mesh.Vertices);
        pass.SetIndexBuffer(mesh.Indices, IndexType.UInt32);
        foreach (var b in mesh.Batches)
        {
            pass.SetBindGroup(0, textureGroup(b.Texture));
            pass.DrawIndexed(b.IndexCount, 1, b.FirstIndex);
        }
    }

    public void Dispose()
    {
        _device.DestroyRenderPipeline(_worldPipeline);
        _device.DestroyRenderPipeline(_carPipeline);
        _device.DestroyTexture(_atlas);
        _device.DestroyShader(_shader);
    }
}
