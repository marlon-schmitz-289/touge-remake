using System.Numerics;

namespace Touge.Formats.Tests;

public class CarPartsTests
{
    /// <summary>Right wheels = left tire turned 180° about Y: its outer face (+X) ends up outside on the right (−X), not mirrored.</summary>
    [Fact]
    public void Right_wheels_are_rotated_not_mirrored()
    {
        var body = new Mesh
        {
            Textures = [], Materials = [],
            Nodes = [.. CarParts.WheelNodes.Select(n => (n, Matrix4x4.CreateTranslation(n.EndsWith("_l") ? 0.7f : -0.7f, 0, n.StartsWith("fr") ? 1.3f : -1.1f)))],
        };
        var w = CarParts.Wheels(body);
        var outer = new Vector3(0.1f, 0, 0);
        Assert.Equal(0.8f, Vector3.Transform(outer, w[0]).X, 5);
        Assert.Equal(-0.8f, Vector3.Transform(outer, w[1]).X, 5);
        Assert.Equal(-0.8f, Vector3.Transform(outer, w[3]).X, 5);
        Assert.True(w[1].GetDeterminant() > 0);
        Assert.True(CarParts.IsDefaultBody("body00") && !CarParts.IsDefaultBody("bodyshd00") && !CarParts.IsDefaultBody("Bcali00FL"));
    }
}
