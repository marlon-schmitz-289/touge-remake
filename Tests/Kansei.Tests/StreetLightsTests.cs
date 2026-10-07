using System.Numerics;
using Kansei.Graphics;

namespace Kansei.Tests;

public class StreetLightsTests
{
    /// <summary>Driving past a row of lamps 30 m apart: a lamp's weight changes by at most 0.1 per 0.5 m, never jumps (the shader's 4 slots swap at weight 0).</summary>
    [Fact]
    public void LampsFadeInsteadOfPopping()
    {
        Vector3[] lamps = [.. Enumerable.Range(0, 12).Select(i => new Vector3(4, 6, i * 30f))];
        Dictionary<Vector3, float> Weights(float z)
        {
            Span<Vector4> p = stackalloc Vector4[4];
            WorldRenderer.PickStreetLights(lamps, new Vector3(0, 1, z), p);
            var w = lamps.ToDictionary(l => l, _ => 0f);
            foreach (var v in p) if (v.W > 0) w[new Vector3(v.X, v.Y, v.Z)] = v.W;
            return w;
        }
        var before = Weights(0);
        Assert.Equal(1, before[lamps[0]]); // the nearest at full strength
        for (var z = 0.5f; z < 300; z += 0.5f)
        {
            var now = Weights(z);
            foreach (var l in lamps) Assert.True(MathF.Abs(now[l] - before[l]) <= 0.1f, $"lamp {l.Z} at z {z}: {before[l]} → {now[l]}");
            before = now;
        }
    }
}
