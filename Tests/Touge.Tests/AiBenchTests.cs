using System.Numerics;
using Touge.Race;
using Xunit;

namespace Touge.Tests;

public class AiBenchTests
{
    /// <summary>Polyline with 5 m spacing: heading +Z, then each (radius, angle) a bend (+ angle = left, towards +X), straights between.</summary>
    private static Vector3[] Line(params (float Straight, float Radius, float Angle)[] parts)
    {
        var pts = new List<Vector3> { Vector3.Zero };
        float heading = 0; // angle from +Z towards +X
        var p = Vector3.Zero;
        foreach (var (straight, radius, angle) in parts)
        {
            for (var d = 5f; d <= straight; d += 5) pts.Add(p += new Vector3(MathF.Sin(heading), 0, MathF.Cos(heading)) * 5);
            var steps = (int)MathF.Ceiling(MathF.Abs(angle) * radius / 5);
            for (var i = 0; i < steps; i++)
            {
                heading += angle / steps;
                pts.Add(p += new Vector3(MathF.Sin(heading), 0, MathF.Cos(heading)) * (MathF.Abs(angle) * radius / steps));
            }
        }
        for (var d = 5f; d <= 100; d += 5) pts.Add(p += new Vector3(MathF.Sin(heading), 0, MathF.Cos(heading)) * 5);
        return [.. pts];
    }

    [Fact]
    public void Corners_finds_kind_direction_and_angle()
    {
        var cs = AiBench.Corners(Line((100, 15, MathF.PI), (100, 60, -MathF.PI / 2)));
        Assert.Equal(2, cs.Count);
        Assert.Equal(("hairpin", 1), (cs[0].Kind, cs[0].Dir));
        Assert.InRange(cs[0].Radius, 12, 18);
        Assert.InRange(cs[0].Angle, 0.85f * MathF.PI, 1.15f * MathF.PI);
        Assert.Equal(("medium", -1), (cs[1].Kind, cs[1].Dir));
        Assert.InRange(cs[1].Angle, 0.4f * MathF.PI, 0.6f * MathF.PI);
        Assert.True(cs[0].To < cs[1].From);
    }
}
