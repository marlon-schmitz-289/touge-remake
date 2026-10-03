using System.Numerics;
using Kansei.Graphics;

namespace Kansei.Tests;

public class NormalsTests
{
    [Fact]
    public void SmoothsGentleBends_KeepsCreases_IgnoresWinding()
    {
        // two triangles sharing the edge x = 0: left flat, right tilted up by `deg` around z
        static Vector3[] Hinge(float deg, bool flipRight)
        {
            var r = deg * MathF.PI / 180;
            var e = new Vector3(MathF.Cos(r), MathF.Sin(r), 0);
            Vector3 a = new(0, 0, 0), b = new(0, 0, 1), l = new(-1, 0, 0);
            Vector3[] right = flipRight ? [a, e, b] : [a, b, e];
            return [a, l, b, .. right];
        }

        var gentle = Normals.Smooth(Hinge(20, false));
        // on the shared edge both faces get the same averaged normal, 10° from vertical (sign follows each face's winding)
        Assert.Equal(MathF.Abs(gentle[0].Y), MathF.Cos(10 * MathF.PI / 180), 3);
        Assert.Equal(1, MathF.Abs(Vector3.Dot(gentle[0], gentle[3])), 3);
        // the far corners touch only their own face
        Assert.Equal(1, MathF.Abs(gentle[1].Y), 3);

        var flipped = Normals.Smooth(Hinge(20, true));
        Assert.Equal(MathF.Abs(gentle[0].Y), MathF.Abs(flipped[0].Y), 3);

        var crease = Normals.Smooth(Hinge(90, false));
        Assert.Equal(1, MathF.Abs(crease[0].Y), 3); // flat side stays flat
        Assert.Equal(1, MathF.Abs(crease[3].X), 3); // vertical side keeps its own normal
    }
}
