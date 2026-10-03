using System.Numerics;
using System.Runtime.InteropServices;
using Penelope;

namespace Kansei.Graphics;

/// <summary>
///     Draws <see cref="Effects"/> into an open <see cref="WorldRenderer.BeginScene"/> pass after the opaque geometry:
///     skid marks and smoke alpha-blended, sparks additive (HDR, so they bloom); depth tested, not written. One
///     per-frame vertex ring (<see cref="TransientBufferRing"/>) per effect type, quads are written straight into it.
///     Shading in effect.frag (mode in the shared push block's uEye.w): smoke/skids lit by sun (shadowed), ambient
///     and the dynamic lights, fogged like the world.
/// </summary>
public sealed class EffectsRenderer : IDisposable
{
    private enum Kind { Smoke, Skid, Spark }

    private readonly WorldRenderer _world;
    private readonly ShaderHandle _shader;
    private readonly RenderPipelineHandle[,] _pipeline = new RenderPipelineHandle[3, 2]; // kind × quality
    private readonly TransientBufferRing[] _rings = new TransientBufferRing[3];

    public EffectsRenderer(WorldRenderer world)
    {
        _world = world;
        var device = world.Device;
        _shader = device.CreateShader(ShaderLoader.LoadGraphics(typeof(EffectsRenderer).Assembly, "world", "effect", "effect"));
        BlendState[] blend = [BlendState.AlphaBlend, BlendState.AlphaBlend, BlendState.Additive];
        int[] capacity = [Effects.MaxSmoke, Effects.MaxSkids, Effects.MaxSparks];
        for (var k = 0; k < 3; k++)
        {
            for (var q = 0; q < 2; q++)
                _pipeline[k, q] = world.ScenePipeline(_shader, WorldVertex.Layout, MultisampleState.Disabled with { SampleCount = WorldRenderer.Samples(q) },
                    true, WorldRenderer.PushBytes, $"effect-{(Kind)k}", blend[k]);
            _rings[k] = new TransientBufferRing(device, capacity[k] * 6 * WorldVertex.Size, BufferUsage.Vertex, $"effect-{(Kind)k}");
        }
    }

    /// <summary>Skid marks, then smoke (sorted back to front), then sparks. <paramref name="view"/> gives the billboard axes.</summary>
    public void Draw(IRenderPassEncoder pass, Effects fx, in Matrix4x4 view, in Matrix4x4 viewProj, Vector3 eye)
    {
        var right = new Vector3(view.M11, view.M21, view.M31); // camera axes = columns of the view rotation
        var up = new Vector3(view.M12, view.M22, view.M32);
        if (fx.SkidCount > 0)
        {
            var a = Allocate(Kind.Skid, fx.SkidCount);
            Submit(pass, Kind.Skid, a.Offset, fx.BuildSkids(Vertices(a)), viewProj, eye);
        }
        if (fx.SmokeCount > 0)
        {
            var a = Allocate(Kind.Smoke, fx.SmokeCount);
            Submit(pass, Kind.Smoke, a.Offset, fx.BuildSmoke(Vertices(a), eye, right, up), viewProj, eye);
        }
        if (fx.SparkCount > 0)
        {
            var a = Allocate(Kind.Spark, fx.SparkCount);
            Submit(pass, Kind.Spark, a.Offset, fx.BuildSparks(Vertices(a), eye), viewProj, eye);
        }
    }

    private TransientAlloc Allocate(Kind kind, int quads) => _rings[(int)kind].Allocate(quads * 6 * WorldVertex.Size, 16);

    private static Span<WorldVertex> Vertices(in TransientAlloc a) => MemoryMarshal.Cast<byte, WorldVertex>(a.Write);

    private void Submit(IRenderPassEncoder pass, Kind kind, int offset, int vertices, in Matrix4x4 viewProj, Vector3 eye)
    {
        if (vertices == 0) return;
        pass.SetPipeline(_pipeline[(int)kind, _world.Quality]);
        _world.BindScene(pass);
        Span<byte> push = stackalloc byte[WorldRenderer.PushBytes];
        _world.WritePush(push, viewProj, Matrix4x4.Identity, eye, false, true);
        MemoryMarshal.Write(push[156..], (float)kind); // uEye.w = effect mode
        pass.SetPushConstants(ShaderStage.Vertex | ShaderStage.Fragment, 0, push);
        pass.SetVertexBuffer(0, _rings[(int)kind].Buffer, offset);
        pass.Draw(vertices);
    }

    public void Dispose()
    {
        foreach (var p in _pipeline) _world.Device.DestroyRenderPipeline(p);
        foreach (var r in _rings) r.Dispose();
        _world.Device.DestroyShader(_shader);
    }
}
