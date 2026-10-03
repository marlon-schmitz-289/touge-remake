using System.Numerics;
using System.Runtime.InteropServices;
using Penelope;

namespace Kansei.Graphics;

/// <summary>
///     Lit car meshes (<see cref="CarVertex"/>): texture × material colour × sun + ambient, fake sky/ground
///     reflection + HDR specular scaled by gloss, alpha test, everything opaque. Light comes from the renderer's
///     <see cref="Atmosphere"/>. Draws into an open <see cref="WorldRenderer.BeginScene"/> pass (same depth buffer,
///     reversed-Z) and uses its textures.
///     Lighting runs in model space (sun/eye/up are transformed on the CPU), so models must be rigid.
/// </summary>
public sealed class CarRenderer : IDisposable
{
    private const int PushBytes = 128; // mvp 64 + sun 16 + eye 16 + up 16 + sky 16

    private readonly WorldRenderer _world;
    private readonly ShaderHandle _shader;
    private readonly RenderPipelineHandle[] _pipeline = new RenderPipelineHandle[2];

    public CarRenderer(WorldRenderer world)
    {
        _world = world;
        var device = world.Device;
        _shader = device.CreateShader(ShaderLoader.LoadGraphics(typeof(CarRenderer).Assembly, "car", "car", "car"));
        for (var q = 0; q < 2; q++)
            _pipeline[q] = world.ScenePipeline(_shader, CarVertex.Layout, MultisampleState.Disabled with { SampleCount = WorldRenderer.Samples(q) },
                true, PushBytes, "car");
    }

    /// <summary>Body at <paramref name="body"/>, the wheel mesh once per entry of <paramref name="wheels"/> (world matrices).</summary>
    public void Draw(IRenderPassEncoder pass, StaticMesh bodyMesh, StaticMesh wheelMesh, in Matrix4x4 body, ReadOnlySpan<Matrix4x4> wheels,
        in Matrix4x4 viewProj, Vector3 eye)
    {
        pass.SetPipeline(_pipeline[_world.Quality]);
        DrawMesh(pass, bodyMesh, body, viewProj, eye);
        foreach (ref readonly var w in wheels) DrawMesh(pass, wheelMesh, w, viewProj, eye);
    }

    private void DrawMesh(IRenderPassEncoder pass, StaticMesh mesh, in Matrix4x4 model, in Matrix4x4 viewProj, Vector3 eye)
    {
        Matrix4x4.Invert(model, out var inv);
        Span<byte> push = stackalloc byte[PushBytes];
        MemoryMarshal.Write(push, model * viewProj);
        var a = _world.Atmosphere;
        MemoryMarshal.Write(push[64..], new Vector4(Vector3.Normalize(Vector3.TransformNormal(a.SunDirection, inv)), a.SunIntensity));
        MemoryMarshal.Write(push[80..], new Vector4(Vector3.Transform(eye, inv), 0));
        MemoryMarshal.Write(push[96..], new Vector4(Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, inv)), 0));
        MemoryMarshal.Write(push[112..], new Vector4(a.Ambient, 0));
        pass.SetPushConstants(ShaderStage.Vertex | ShaderStage.Fragment, 0, push);
        pass.SetVertexBuffer(0, mesh.Vertices);
        pass.SetIndexBuffer(mesh.Indices, IndexType.UInt32);
        foreach (var b in mesh.Batches)
        {
            pass.SetBindGroup(0, _world.TextureGroup(b.Texture));
            pass.DrawIndexed(b.IndexCount, 1, b.FirstIndex);
        }
    }

    public void Dispose()
    {
        foreach (var p in _pipeline) _world.Device.DestroyRenderPipeline(p);
        _world.Device.DestroyShader(_shader);
    }
}
