using System.Numerics;
using Kansei.Graphics;

namespace Kansei.Tests;

public class ShadowMapTests
{
    [Fact]
    public void Fit_CoversTheSphere_AndMovesOnlyByWholeTexels()
    {
        var toSun = Vector3.Normalize(new Vector3(0.4f, 0.8f, 0.3f));
        const float radius = 14;
        const int res = 2048;
        var world = new Vector3(103.7f, 21.2f, -58.9f); // a fixed point the shadow map sees
        var rng = new Random(1);
        var first = Texel(ShadowMap.Fit(world, radius, toSun, res, 250), world, res);
        for (var i = 0; i < 50; i++)
        {
            // camera wanders a few metres: the point's texel position may only change by whole texels (no crawling)
            var centre = world + new Vector3(rng.NextSingle() - 0.5f, rng.NextSingle() - 0.5f, rng.NextSingle() - 0.5f) * 8;
            var m = ShadowMap.Fit(centre, radius, toSun, res, 250);
            var t = Texel(m, world, res) - first;
            Assert.Equal(MathF.Round(t.X), t.X, 0.01f);
            Assert.Equal(MathF.Round(t.Y), t.Y, 0.01f);

            // everything within the sphere lands inside the tile and the depth range
            var edge = Vector4.Transform(new Vector4(centre + Vector3.Normalize(new Vector3(1, -2, 0.5f)) * radius, 1), m);
            Assert.InRange(edge.X, -1, 1);
            Assert.InRange(edge.Y, -1, 1);
            Assert.InRange(edge.Z, 0, 1);
        }
    }

    private static Vector2 Texel(Matrix4x4 m, Vector3 p, int res)
    {
        var c = Vector4.Transform(new Vector4(p, 1), m);
        return new Vector2(c.X, c.Y) * (res / 2f);
    }
}
