using System.Numerics;
using Kansei.Graphics;

namespace Kansei.Tests;

public class FrustumTests
{
    [Fact]
    public void Visible_CullsOnlyBoxesFullyOutside_ForCameraAndShadowCascade()
    {
        var eye = new Vector3(10, 5, -20);
        var camera = Matrix4x4.CreateLookAt(eye, eye + Vector3.UnitZ, Vector3.UnitY) * WorldRenderer.Perspective(MathF.PI / 3, 16f / 9, 0.3f, false);
        var cascade = ShadowMap.Fit(eye + Vector3.UnitZ * 20, 30, Vector3.Normalize(new Vector3(0.4f, 0.8f, 0.3f)), 2048, 250);

        static (Vector3, Vector3) Box(Vector3 c, float h) => (c - new Vector3(h), c + new Vector3(h));
        Assert.True(Frustum.Visible(camera, Box(eye + new Vector3(0, 0, 3000), 5).Item1, Box(eye + new Vector3(0, 0, 3000), 5).Item2)); // far ahead: no far plane
        Assert.False(Frustum.Visible(camera, Box(eye - Vector3.UnitZ * 50, 5).Item1, Box(eye - Vector3.UnitZ * 50, 5).Item2)); // behind
        Assert.False(Frustum.Visible(camera, Box(eye + new Vector3(-200, 0, 50), 5).Item1, Box(eye + new Vector3(-200, 0, 50), 5).Item2)); // far left
        Assert.False(Frustum.Visible(cascade, Box(eye + Vector3.UnitZ * 200, 5).Item1, Box(eye + Vector3.UnitZ * 200, 5).Item2)); // beyond the cascade

        // conservative: a box with any point inside the clip volume is never culled
        var rng = new Random(7);
        foreach (var m in new[] { camera, cascade })
            for (var i = 0; i < 2000; i++)
            {
                var min = eye + new Vector3(rng.NextSingle() - 0.5f, rng.NextSingle() - 0.5f, rng.NextSingle() - 0.5f) * 300;
                var max = min + new Vector3(rng.NextSingle(), rng.NextSingle(), rng.NextSingle()) * 64;
                var anyInside = false;
                for (var k = 0; k < 64 && !anyInside; k++)
                {
                    var c = Vector4.Transform(new Vector4(Vector3.Lerp(min, max, rng.NextSingle()) with
                    {
                        Y = float.Lerp(min.Y, max.Y, rng.NextSingle()), Z = float.Lerp(min.Z, max.Z, rng.NextSingle()),
                    }, 1), m);
                    anyInside = MathF.Abs(c.X) <= c.W && MathF.Abs(c.Y) <= c.W && c.Z >= 0 && c.Z <= c.W;
                }
                if (anyInside) Assert.True(Frustum.Visible(m, min, max));
            }
    }
}
