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

    /// <summary>8 m wide road x ∈ [−4, 4], z ∈ [−20, 300], walls along both sides.</summary>
    static readonly TriangleGround Road = WallTests.Grid([-4, 4], [-20, 300], (_, _) => false);

    static int Contacts(Vehicle car, IGround ground)
    {
        Span<Vector3> probes = stackalloc Vector3[4];
        Span<WallContact> contacts = stackalloc WallContact[8];
        car.WallProbes(probes);
        return ground.CollideWalls(probes, Vehicle.ProbeRadius, contacts);
    }

    [Fact]
    public void Spawn_skips_points_in_walls_and_faces_the_line_direction()
    {
        // points 0..9 hug the left wall (body would overlap it), from 10 on the centre line
        var line = new Vector3[25];
        for (var i = 0; i < line.Length; i++) line[i] = new Vector3(i < 10 ? 3.6f : 0, 0, i * 10);
        var car = new Vehicle(CarSpec.AE86);
        Assert.Equal(10, new LinePilot(line).Spawn(car, Road, 2));
        Assert.Equal(0, Contacts(car, Road));
        Assert.True(Vector3.Transform(Vector3.UnitZ, car.Orientation).Z > 0.99f);

        // reverse direction: the same road driven the other way faces −Z
        var back = line.Reverse().ToArray();
        var at = new LinePilot(back).Spawn(car, Road, 0);
        Assert.Equal(0, at);
        Assert.True(Vector3.Transform(Vector3.UnitZ, car.Orientation).Z < -0.99f);
    }

    [Fact]
    public void Reset_frees_a_car_stuck_in_the_wall()
    {
        var line = new Vector3[25];
        for (var i = 0; i < line.Length; i++) line[i] = new Vector3(0, 0, i * 10);
        var pilot = new LinePilot(line);
        var car = new Vehicle(CarSpec.AE86);
        car.Reset(new Vector3(1, 0, 90), 1.2f); // full throttle and lock into the left wall
        for (var t = 0; t < 360; t++) car.Step(new VehicleInput(1, 0, -1), Road, 1f / 120);
        Assert.True(car.WallContacts > 0 && car.SpeedKmh < 10, $"not stuck: {car.SpeedKmh:F0} km/h at {car.Position}");

        // what Drive.ResetNearest does: tracked segment while near the line, else global nearest
        var (_, lateral) = pilot.Track(car.Position);
        var at = pilot.Spawn(car, Road, MathF.Abs(lateral) < 15 ? pilot.Segment : pilot.Nearest(car.Position));
        log.WriteLine($"reset to point {at}, pos {car.Position}");
        Assert.InRange(at, 8, 10);
        Assert.Equal(0, Contacts(car, Road));
        var z0 = car.Position.Z;
        var walls = 0;
        for (var t = 0; t < 300; t++) // 2.5 s: the engine first spins up to launch revs through the slipping clutch
        {
            car.Step(pilot.Drive(car), Road, 1f / 120);
            walls += car.WallContacts;
        }
        Assert.Equal(0, walls);
        Assert.True(car.Position.Z - z0 > 10, "does not drive off after the reset");
    }
}
