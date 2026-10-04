using System.Numerics;
using Kansei.Graphics;

namespace Kansei.Tests;

public class EffectsTests
{
    [Fact]
    public void Skids_RingAndFade_SmokeAgesOut()
    {
        var fx = new Effects();
        var side = new Vector3(0.1f, 0, 0);
        // a wheel sliding 0.1 m per tick along +Z: one segment every 0.25 m, the ring keeps the newest MaxSkids
        for (var i = 0; i <= 200_000; i++) fx.Skid(0, new Vector3(0, 0, i * 0.1f), side, Vector3.UnitY, 1);
        Assert.Equal(Effects.MaxSkids, fx.SkidCount);
        var v = new WorldVertex[Effects.MaxSkids * 6];
        Assert.Equal(Effects.MaxSkids * 6, fx.BuildSkids(v));
        Assert.All(v, x => Assert.InRange(x.Position.Z, 20_000 - Effects.MaxSkids * 0.31f, 20_000)); // only recent ones
        var first = v[0];
        Assert.Equal(0.02f, first.Position.Y, 1e-5f); // lifted off the road
        Assert.Equal(0.2f, Vector3.Distance(v[0].Position, v[1].Position), 1e-4f); // tread width

        // a jump (reset) does not draw a strip across the map; strength 0 ends the strip
        var before = fx.SkidCount;
        fx.Skid(1, Vector3.Zero, side, Vector3.UnitY, 1);
        fx.Skid(1, new Vector3(50, 0, 0), side, Vector3.UnitY, 1);
        fx.Skid(1, new Vector3(50, 0, 0.3f), side, Vector3.UnitY, 0);
        Assert.Equal(before, fx.SkidCount);

        // marks hold, then fade out completely; smoke and sparks die after their lifetime
        fx.EmitSmoke(Vector3.Zero, Vector3.Zero, 0.3f, 0.5f);
        fx.EmitSpark(Vector3.Zero, Vector3.UnitY);
        for (var t = 0f; t < Effects.SkidHold - 1; t += 0.5f) fx.Update(0.5f);
        Assert.Equal(0, fx.SmokeCount);
        Assert.Equal(0, fx.SparkCount);
        fx.BuildSkids(v);
        Assert.True(v[0].Color.W > 0.85f);
        for (var t = 0f; t < Effects.SkidFade + 2; t += 0.5f) fx.Update(0.5f);
        Assert.Equal(0, fx.BuildSkids(v));
    }

    [Fact]
    public void Spray_ArcsDownAndDies_SmokeRises()
    {
        var fx = new Effects();
        fx.EmitSpray(Vector3.Zero, new Vector3(0, 3, 0), 0.2f, 1);
        fx.EmitSmoke(new Vector3(0, 0, 10), Vector3.Zero, 0.3f, 1);
        var eye = new Vector3(500, 0, 0); // far along x: the quads' pull towards the eye does not move them in y
        var v = new WorldVertex[12];
        float Height(float z) // centre of the puff near z (mean of its quad's corners)
        {
            var n = fx.BuildSmoke(v, eye, Vector3.UnitZ, Vector3.UnitY);
            var quad = v.Take(n).Chunk(6).Single(q => MathF.Abs(q.Average(x => x.Position.Z) - z) < 3);
            return (quad[0].Position.Y + quad[1].Position.Y + quad[2].Position.Y + quad[5].Position.Y) / 4;
        }
        void Run(float seconds)
        {
            for (var t = 0f; t < seconds; t += 1 / 120f) fx.Update(1 / 120f);
        }

        Run(0.3f);
        var top = Height(0);
        Assert.InRange(top, 0.25f, 0.5f); // thrown up …
        Run(0.4f);
        Assert.True(Height(0) < 0, "spray falls back below where it left the tyre");
        Assert.True(Height(10) > 0.05f, "smoke keeps rising");
        Run(0.6f);
        Assert.Equal(1, fx.SmokeCount); // spray gone after ≤ 1.2 s, smoke lives on
    }
}
