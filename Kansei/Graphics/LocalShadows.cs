using System.Numerics;
using System.Runtime.InteropServices;
using Penelope;

namespace Kansei.Graphics;

/// <summary>
///     Shadows of the local lights: one depth atlas of <see cref="Tiles"/> square tiles side by side — tile 0 the headlights
///     (one projection from the middle between the lamps along the beam axis, <see cref="HeadFov"/>), tiles 1–4 the street
///     lights the renderer picked (straight down from just under the lamp head, <see cref="StreetFov"/>). The projections are
///     simple enough for the shaders to rebuild from the lamp positions they already have (lighting.glsl localShadow), so no
///     matrices go into the per-draw block: clip = (k·x, k·y, A·d + B, d) in the light's frame (x right, y up/along, d = depth
///     along the axis), A = f / (f − n), B = −n·f / (f − n). Casters as in <see cref="ShadowMap"/> (alpha-tested, slope bias),
///     only the batches inside each light's frustum.
/// </summary>
public sealed class LocalShadows : IDisposable
{
    public const int Tiles = 5, TileSize = 1024;
    /// <summary>Full field of view (degrees), near and far plane (m): headlights, street lights. Mirrored in lighting.glsl.</summary>
    public const float HeadFov = 100, HeadNear = 0.5f, HeadFar = 90, StreetFov = 140, StreetNear = 0.3f, StreetFar = 30;
    /// <summary>The street light's shadow starts this far under the lamp point (its own housing casts none).</summary>
    public const float StreetDrop = 0.35f;

    private readonly IPenelopeDevice _device;
    private readonly ShaderHandle _shader;
    private readonly RenderPipelineHandle _worldPipeline, _carPipeline;
    private readonly TextureHandle _atlas;

    /// <summary>World → clip of each tile (unused tiles: default).</summary>
    public readonly Matrix4x4[] ViewProj = new Matrix4x4[Tiles];
    public TextureViewHandle View { get; }
    public SamplerHandle Sampler { get; }

    public LocalShadows(IPenelopeDevice device, BindGroupLayoutHandle textureLayout)
    {
        _device = device;
        _shader = device.CreateShader(ShaderLoader.LoadGraphics(typeof(LocalShadows).Assembly, "shadow", "shadow", "local-shadow"));
        _atlas = device.CreateTexture(new TextureDesc(TileSize * Tiles, TileSize, WorldRenderer.ShadowFormat,
            TextureUsage.DepthStencilAttachment | TextureUsage.Sampled, DebugName: "local-shadow-atlas"));
        View = device.DefaultTextureView(_atlas);
        Sampler = device.GetSampler(SamplerDesc.Linear with { Compare = CompareFunc.LessEqual, LodMaxClamp = 0 });
        _worldPipeline = Pipeline(WorldVertex.Size, 12, textureLayout);
        _carPipeline = Pipeline(CarVertex.Size, 24, textureLayout);
    }

    private RenderPipelineHandle Pipeline(int stride, int uvOffset, BindGroupLayoutHandle textureLayout) =>
        _device.CreateRenderPipeline(new RenderPipelineDesc(_shader,
            VertexLayout.Interleaved(stride, new VertexAttribute(0, VertexFormat.Float3, 0), new VertexAttribute(1, VertexFormat.Float2, uvOffset)),
            PrimitiveTopology.TriangleList, RasterizerState.Default,
            DepthStencilState.DepthLessWrite with { DepthBiasConstant = 2, DepthBiasSlope = 2f, DepthBiasClamp = 0.01f },
            MultisampleState.Disabled, [], WorldRenderer.ShadowFormat, [textureLayout],
            [new PushConstantRange(ShaderStage.Vertex | ShaderStage.Fragment, 0, 80)], "local-shadow"));

    /// <summary>World → clip of a light at <paramref name="pos"/> looking along <paramref name="forward"/> with <paramref name="right"/> and <paramref name="up"/> as the tile's x and y.</summary>
    public static Matrix4x4 Projection(Vector3 pos, Vector3 right, Vector3 up, Vector3 forward, float fovDeg, float near, float far)
    {
        var k = 1 / MathF.Tan(fovDeg * MathF.PI / 360);
        float a = far / (far - near), b = -near * far / (far - near);
        // rows = world x, y, z, 1 (row-vector convention); columns = clip x, y, z, w
        var p = new Matrix4x4(
            k * right.X, k * up.X, a * forward.X, forward.X,
            k * right.Y, k * up.Y, a * forward.Y, forward.Y,
            k * right.Z, k * up.Z, a * forward.Z, forward.Z,
            0, 0, b, 0);
        return Matrix4x4.CreateTranslation(-pos) * p;
    }

    /// <summary>Headlights: from the middle of the lamps along the (horizontal) beam axis.</summary>
    public static Matrix4x4 Head(Vector3 middle, Vector3 axis)
    {
        var right = Vector3.Normalize(Vector3.Cross(axis, Vector3.UnitY));
        return Projection(middle, right, Vector3.Cross(right, axis), axis, HeadFov, HeadNear, HeadFar);
    }

    /// <summary>A street light: straight down from <see cref="StreetDrop"/> under the lamp point, tile x = world x, y = world z.</summary>
    public static Matrix4x4 Street(Vector3 lamp) =>
        Projection(lamp - new Vector3(0, StreetDrop, 0), Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitY, StreetFov, StreetNear, StreetFar);

    /// <summary>
    ///     Renders the tiles of the lights in use: the headlights when <paramref name="headAxis"/> is non-zero, each street light
    ///     with weight &gt; 0 (<paramref name="streets"/> as WorldRenderer picks them). Returns the number of draws.
    /// </summary>
    public int Render(ICommandEncoder encoder, Func<int, BindGroupHandle> textureGroup, StaticMesh world, ReadOnlySpan<(StaticMesh Mesh, Matrix4x4 Model)> cars,
        Vector3 headMiddle, Vector3 headAxis, ReadOnlySpan<Vector4> streets)
    {
        using var pass = encoder.BeginRenderPass(new RenderPassDesc([],
            new DepthStencilAttachment(View, LoadOp.Clear, StoreOp.Store, 1f, LoadOp.Clear, StoreOp.DontCare, 0, false, false),
            DebugName: "local-shadows"));
        Span<byte> push = stackalloc byte[80];
        var draws = 0;
        for (var t = 0; t < Tiles; t++)
        {
            if (t == 0 ? headAxis == Vector3.Zero : streets[t - 1].W <= 0) continue;
            ViewProj[t] = t == 0 ? Head(headMiddle, headAxis) : Street(new Vector3(streets[t - 1].X, streets[t - 1].Y, streets[t - 1].Z));
            pass.SetViewport(t * TileSize, 0, TileSize, TileSize);
            pass.SetScissor(t * TileSize, 0, TileSize, TileSize);
            pass.SetPipeline(_worldPipeline);
            draws += Draw(pass, world, ViewProj[t], 0.3f, textureGroup, push);
            pass.SetPipeline(_carPipeline);
            foreach (var (mesh, model) in cars) draws += Draw(pass, mesh, model * ViewProj[t], 0.5f, textureGroup, push);
        }
        return draws;
    }

    private static int Draw(IRenderPassEncoder pass, StaticMesh mesh, in Matrix4x4 mvp, float cutoff, Func<int, BindGroupHandle> textureGroup, Span<byte> push)
    {
        MemoryMarshal.Write(push, in mvp);
        MemoryMarshal.Write(push[64..], new Vector4(cutoff, 0, 0, 0));
        pass.SetPushConstants(ShaderStage.Vertex | ShaderStage.Fragment, 0, push);
        pass.SetVertexBuffer(0, mesh.Vertices);
        pass.SetIndexBuffer(mesh.Indices, IndexType.UInt32);
        int draws = 0, texture = -1;
        var bounds = mesh.Bounds;
        for (var i = 0; i < mesh.Batches.Count; i++)
        {
            if (bounds != null && !Frustum.Visible(mvp, bounds[i].Min, bounds[i].Max)) continue;
            var b = mesh.Batches[i];
            if (b.Texture != texture) pass.SetBindGroup(0, textureGroup(texture = b.Texture));
            pass.DrawIndexed(b.IndexCount, 1, b.FirstIndex);
            draws++;
        }
        return draws;
    }

    public void Dispose()
    {
        _device.DestroyRenderPipeline(_worldPipeline);
        _device.DestroyRenderPipeline(_carPipeline);
        _device.DestroyTexture(_atlas);
        _device.DestroyShader(_shader);
    }
}
