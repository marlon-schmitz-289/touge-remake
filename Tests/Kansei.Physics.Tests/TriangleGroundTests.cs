using System.Numerics;
using Kansei.Physics;

namespace Kansei.Physics.Tests;

public class TriangleGroundTests
{
    /// <summary>10 × 10 m road square (two tris, surfaces 7/8) with a wall tri beyond its +X edge.</summary>
    [Fact]
    public void Raycast_hits_road_and_walls_push_toward_road()
    {
        Vector3[] pos = [new(0, 0, 0), new(10, 0, 0), new(10, 0, 10), new(0, 0, 10), new(20, 0, 5)];
        var g = new TriangleGround(pos, [0, 3, 2, 0, 2, 1, 1, 4, 2], [7, 8, 14], [false, false, true]);

        Assert.True(g.Raycast(new Vector3(2, 5, 8), -Vector3.UnitY, 10, out var hit));
        Assert.Equal(7, hit.Surface);
        Assert.Equal(5f, hit.Distance, 1e-5f);
        Assert.Equal(Vector3.UnitY, hit.Normal);
        Assert.True(g.Raycast(new Vector3(8, 5, 2), -Vector3.UnitY, 10, out hit));
        Assert.Equal(8, hit.Surface);
        Assert.False(g.Raycast(new Vector3(15, 5, 5), -Vector3.UnitY, 10, out _)); // wall tri is not drivable
        Assert.False(g.Raycast(new Vector3(2, 5, 8), -Vector3.UnitY, 4, out _)); // out of range

        Assert.Equal(4, g.Walls.Length); // road/wall edge + three open borders, the shared diagonal is none
        Span<WallContact> c = stackalloc WallContact[4];
        Assert.Equal(1, g.CollideWalls([new Vector3(9.7f, 0.5f, 5), new Vector3(5, 0.5f, 5)], 0.5f, c));
        Assert.Equal(0, c[0].ProbeIndex);
        Assert.Equal(-Vector3.UnitX, c[0].Normal);
        Assert.Equal(0.2f, c[0].Depth, 1e-5f);
    }

    [Fact]
    public void Seam_with_repeated_vertices_is_no_wall()
    {
        // two road quads meeting at x = 10, the second with its own copies of the seam vertices (like MYOUGI's material seams)
        Vector3[] pos = [new(0, 0, 0), new(10, 0, 0), new(10, 0, 10), new(0, 0, 10), new(10, 0, 0), new(20, 0, 0), new(20, 0, 10), new(10, 0, 10)];
        var g = new TriangleGround(pos, [0, 3, 2, 0, 2, 1, 4, 7, 6, 4, 6, 5], [0, 0, 1, 1], [false, false, false, false]);
        Assert.Equal(6, g.Walls.Length); // outer border only
        Assert.DoesNotContain(g.Walls, w => w.A.X == 10 && w.B.X == 10);
    }
}
