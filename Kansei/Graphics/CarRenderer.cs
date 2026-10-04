using System.Numerics;
using Penelope;

namespace Kansei.Graphics;

/// <summary>
///     Lit car meshes (<see cref="CarVertex"/>, car.frag): diffuse with sun shadows and dynamic lights, clear coat with
///     Fresnel env-map reflections on paint and glass, glowing lamps. Light comes from the renderer's
///     <see cref="Atmosphere"/>/<see cref="SceneLights"/>. Draws into an open <see cref="WorldRenderer.BeginScene"/>
///     pass (same depth buffer, reversed-Z) and uses its textures. Models must be rigid (normals use the model rotation).
/// </summary>
public sealed class CarRenderer : IDisposable
{
    private readonly WorldRenderer _world;
    private readonly ShaderHandle _shader;
    private readonly RenderPipelineHandle[] _pipeline = new RenderPipelineHandle[2], _blend = new RenderPipelineHandle[2], _ghost = new RenderPipelineHandle[2], _ghostDepth = new RenderPipelineHandle[2];

    /// <summary>See-through blend for <see cref="DrawGhost"/>: colour × constant colour (tint × alpha) + scene × (1 − constant alpha).</summary>
    private static readonly BlendState GhostBlend = new(true, BlendFactor.ConstantColor, BlendFactor.OneMinusConstantAlpha, BlendOp.Add,
        BlendFactor.Zero, BlendFactor.One, BlendOp.Add, ColorWriteMask.All);

    /// <summary>Depth only (the ghost's nearest surface, so only that one blends).</summary>
    private static readonly BlendState DepthOnly = BlendState.Opaque with { WriteMask = ColorWriteMask.None };

    public CarRenderer(WorldRenderer world)
    {
        _world = world;
        var device = world.Device;
        _shader = device.CreateShader(ShaderLoader.LoadGraphics(typeof(CarRenderer).Assembly, "car", "car", "car"));
        for (var q = 0; q < 2; q++)
        {
            var ms = MultisampleState.Disabled with { SampleCount = WorldRenderer.Samples(q) };
            _pipeline[q] = world.ScenePipeline(_shader, CarVertex.Layout, ms, true, "car");
            _blend[q] = world.ScenePipeline(_shader, CarVertex.Layout, ms, true, "car-decals", BlendState.AlphaBlend);
            _ghost[q] = world.ScenePipeline(_shader, CarVertex.Layout, ms with { AlphaToCoverageEnabled = false }, true, "car-ghost", GhostBlend);
            _ghostDepth[q] = world.ScenePipeline(_shader, CarVertex.Layout, ms with { AlphaToCoverageEnabled = false }, true, "car-ghost-depth", DepthOnly, depthWrite: true);
        }
    }

    /// <summary>
    ///     Body at <paramref name="body"/>, the wheel mesh once per entry of <paramref name="wheels"/> (world matrices),
    ///     then the body's <paramref name="decalMesh"/> alpha-blended on top (stickers, badges).
    /// </summary>
    public void Draw(IRenderPassEncoder pass, StaticMesh bodyMesh, StaticMesh decalMesh, StaticMesh wheelMesh, in Matrix4x4 body,
        ReadOnlySpan<Matrix4x4> wheels, in Matrix4x4 viewProj, Vector3 eye)
    {
        pass.SetPipeline(_pipeline[_world.Quality]);
        DrawMesh(pass, bodyMesh, body, viewProj, eye);
        foreach (ref readonly var w in wheels) DrawMesh(pass, wheelMesh, w, viewProj, eye);
        if (decalMesh.Batches.Count == 0) return;
        pass.SetPipeline(_blend[_world.Quality]);
        DrawMesh(pass, decalMesh, body, viewProj, eye);
    }

    /// <summary>
    ///     A see-through car (time attack ghost): first its depth alone, then body and wheels lit as usual and blended over
    ///     the scene where they are nearest (one layer, not every inner face), tinted by <paramref name="tint"/> at
    ///     <paramref name="alpha"/>. No decals.
    /// </summary>
    public void DrawGhost(IRenderPassEncoder pass, StaticMesh bodyMesh, StaticMesh wheelMesh, in Matrix4x4 body, ReadOnlySpan<Matrix4x4> wheels,
        in Matrix4x4 viewProj, Vector3 eye, float alpha, Vector3 tint)
    {
        for (var p = 0; p < 2; p++)
        {
            pass.SetPipeline(p == 0 ? _ghostDepth[_world.Quality] : _ghost[_world.Quality]);
            pass.SetBlendConstant(tint.X * alpha, tint.Y * alpha, tint.Z * alpha, alpha);
            DrawMesh(pass, bodyMesh, body, viewProj, eye);
            foreach (ref readonly var w in wheels) DrawMesh(pass, wheelMesh, w, viewProj, eye);
        }
    }

    /// <summary>A rigid part on its own matrix (pop-up headlamps), opaque, after <see cref="Draw"/>.</summary>
    public void DrawPart(IRenderPassEncoder pass, StaticMesh mesh, in Matrix4x4 model, in Matrix4x4 viewProj, Vector3 eye)
    {
        pass.SetPipeline(_pipeline[_world.Quality]);
        DrawMesh(pass, mesh, model, viewProj, eye);
    }

    private void DrawMesh(IRenderPassEncoder pass, StaticMesh mesh, in Matrix4x4 model, in Matrix4x4 viewProj, Vector3 eye)
    {
        Span<byte> push = stackalloc byte[WorldRenderer.PushBytes];
        _world.WritePush(push, model * viewProj, model, eye, false, true);
        pass.SetVertexBuffer(0, mesh.Vertices);
        pass.SetIndexBuffer(mesh.Indices, IndexType.UInt32);
        _world.DrawBatches(pass, mesh, push, model * viewProj, eye);
    }

    public void Dispose()
    {
        foreach (var p in _pipeline.Concat(_blend).Concat(_ghost).Concat(_ghostDepth)) _world.Device.DestroyRenderPipeline(p);
        _world.Device.DestroyShader(_shader);
    }
}
