using System.Numerics;

namespace Touge.Formats.Tests;

public class ZFightTests
{
    /// <summary>
    ///     Ground triangle, a decal 0.2 mm above it, a decal on the decal, a patch 5 mm above (found, but too far to
    ///     fight), the ground's neighbour (shared edge, no overlap) and a vertical card (not coplanar).
    /// </summary>
    [Fact]
    public void Finds_coplanar_overlaps_and_layers_later_ones_on_top()
    {
        static Vector3 V(float x, float y, float z) => new(x, y, z);
        Vector3[] c =
        [
            V(0, 0, 0), V(4, 0, 0), V(0, 0, 4),                       // 0 ground
            V(1, 0.0002f, 1), V(2, 0.0002f, 1), V(1, 0.0002f, 2),     // 1 decal
            V(1.2f, 0.0004f, 1.2f), V(1.6f, 0.0004f, 1.2f), V(1.2f, 0.0004f, 1.6f), // 2 decal on decal
            V(0.2f, 0.005f, 3), V(0.7f, 0.005f, 3), V(0.2f, 0.005f, 3.5f), // 3 patch 5 mm up
            V(4, 0, 0), V(4, 0, 4), V(0, 0, 4),                       // 4 neighbour
            V(1, -1, 1.5f), V(2, 1, 1.5f), V(1, 1, 1.5f),             // 5 vertical
        ];

        var pairs = ZFight.Find(c);
        Assert.Equal([(0, 1), (0, 2), (1, 2), (0, 3)], pairs.Select(p => (p.A, p.B)));
        Assert.InRange(pairs[3].Gap, 0.0049f, 0.0051f);
        Assert.InRange(pairs[0].Area, 0.49f, 0.51f);

        var layers = ZFight.Layers(6, pairs);
        Assert.Equal([0, 1, 2, 0, 0, 0], layers);

        var ranges = ZFight.Order([0, 1, 3], layers, out var order);
        Assert.Equal([0, 3, 4, 5, 1, 2], order);
        Assert.Equal([(0, 0, 0, 1), (2, 0, 1, 3), (1, 1, 4, 1), (1, 2, 5, 1)], ranges);
    }
}
