using System.Numerics;
using Xunit.Abstractions;

namespace Kansei.Physics.Tests;

public class LinePilotTests(ITestOutputHelper log)
{
    [Fact]
    public void FollowsWindingLine()
    {
        // slalom line, 10 m spacing like the game's driving lines: x = 15·sin(z/40) → min radius ≈ 107 m
        var line = new Vector3[150];
        for (var i = 0; i < line.Length; i++) line[i] = new Vector3(15 * MathF.Sin(i * 10 / 40f), 0, i * 10);
        var pilot = new LinePilot(line);
        var car = new Vehicle(CarSpec.AE86);
        car.Reset(Vector3.Zero, 0);
        pilot.Nearest(car.Position);

        float maxLat = 0, s = 0;
        for (var t = 0f; t < 40; t += 1f / 120)
        {
            car.Step(pilot.Drive(car), new FlatGround(), 1f / 120);
            (s, var lat) = pilot.Track(car.Position);
            if (t > 3) maxLat = MathF.Max(maxLat, MathF.Abs(lat));
        }

        log.WriteLine($"along {s:F0} m of {pilot.Length:F0}, max |lateral| {maxLat:F2} m, {car.SpeedKmh:F0} km/h");
        Assert.True(s > 1000);
        Assert.InRange(maxLat, 0, 3f);
        // left of the line is +lateral
        Assert.True(pilot.Track(line[50] + new Vector3(3, 0, 0)).Lateral > 0);
    }
}
