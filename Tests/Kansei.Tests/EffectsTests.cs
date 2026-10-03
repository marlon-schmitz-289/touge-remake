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
}
