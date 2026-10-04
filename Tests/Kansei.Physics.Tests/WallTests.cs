using System.Numerics;
using Xunit.Abstractions;

namespace Kansei.Physics.Tests;

/// <summary>Body outline vs <see cref="TriangleGround" /> walls: posts between the corner probes, speed, allocations.</summary>
public class WallTests(ITestOutputHelper log)
{
    const float Dt = 1f / 120;

    /// <summary>Flat y = 0 grid with cell borders at <paramref name="xs" /> × <paramref name="zs" />; <paramref name="wall" />(i, j) cells are walls.</summary>
    internal static TriangleGround Grid(float[] xs, float[] zs, Func<int, int, bool> wall)
    {
        var pos = new List<Vector3>();
        foreach (var z in zs)
        foreach (var x in xs) pos.Add(new Vector3(x, 0, z));
        List<int> idx = [];
        List<bool> isWall = [];
        for (var j = 0; j < zs.Length - 1; j++)
        for (var i = 0; i < xs.Length - 1; i++)
        {
            int a = j * xs.Length + i, b = a + 1, c = a + xs.Length, d = c + 1;
            idx.AddRange([a, c, d, a, d, b]);
            isWall.AddRange([wall(i, j), wall(i, j)]);
        }
        return new TriangleGround([.. pos], [.. idx], new int[isWall.Count], [.. isWall]);
    }

    static Vehicle At150()
    {
        var car = new Vehicle(CarSpec.AE86);
        car.Reset(Vector3.Zero, 0);
        var flat = new FlatGround();
        for (var i = 0; i < 120 * 90 && car.SpeedKmh < 150; i++) car.Step(new VehicleInput(1, 0, 0), flat, Dt);
        Assert.True(car.SpeedKmh >= 150, $"only {car.SpeedKmh:F0} km/h");
        return car;
    }

    [Theory]
    [InlineData(40f)]
    [InlineData(150f)]
    public void Post_between_front_corners_stops_the_car(float kmh)
    {
        // 30 cm post on the car's centre line: the corner spheres (x = ±0.515, r 0.3) leave a 43 cm gap around it
        var car = kmh > 100 ? At150() : new Vehicle(CarSpec.AE86);
        if (kmh <= 100)
        {
            car.Reset(Vector3.Zero, 0);
            while (car.SpeedKmh < kmh) car.Step(new VehicleInput(1, 0, 0), new FlatGround(), Dt);
        }
        var postZ = car.Position.Z + 25;
        var x = car.Position.X;
        var ground = Grid([x - 10, x - 0.15f, x + 0.15f, x + 10], [postZ - 3000, postZ - 0.15f, postZ + 0.15f, postZ + 100], (i, j) => i == 1 && j == 1);
        var maxZ = float.MinValue;
        for (var t = 0f; t < 3; t += Dt)
        {
            car.Step(new VehicleInput(1, 0, 0), ground, Dt);
            maxZ = MathF.Max(maxZ, car.Position.Z);
        }
        var nose = maxZ + CarSpec.AE86.Length / 2;
        log.WriteLine($"{kmh} km/h: nose max {nose - (postZ - 0.15f):F2} m past the post face, now {car.SpeedKmh:F1} km/h, x {car.Position.X - x:F2}");
        Assert.InRange(nose, 0, postZ - 0.15f + 0.3f); // stopped at the post (≤ one probe radius of overlap), never through
    }

    [Fact]
    public void Glancing_hit_at_150_does_not_tunnel()
    {
        var car = At150();
        var (x0, z0) = (car.Position.X, car.Position.Z);
        // 8 m wide strip around the car, walls along both sides; steer left (+X) into the wall at x0 + 4
        var ground = Grid([x0 - 4, x0 + 4], [z0 - 100, z0 + 1000], (_, _) => false);
        float maxX = float.MinValue;
        var contacts = 0;
        for (var t = 0f; t < 3; t += Dt)
        {
            car.Step(new VehicleInput(1, 0, t < 0.6f ? -0.6f : 0), ground, Dt);
            maxX = MathF.Max(maxX, car.Position.X - x0);
            contacts += car.WallContacts;
        }
        log.WriteLine($"glancing: max x {maxX:F2} m (wall at 4), {contacts} contacts, now {car.SpeedKmh:F0} km/h, x {car.Position.X - x0:F2}");
        Assert.True(contacts > 0, "never reached the wall");
        Assert.InRange(maxX, 0, 4 - CarSpec.AE86.Width / 2 + Vehicle.ProbeRadius); // body edge at most one probe radius into the wall
        Assert.InRange(car.Position.X - x0, -4, 4 - CarSpec.AE86.Width / 2 + 0.05f);
    }

    [Fact]
    public void Wall_contact_ticks_do_not_allocate()
    {
        var car = new Vehicle(CarSpec.AE86);
        var ground = Grid([-4, -0.15f, 0.15f, 4], [-10, 9.85f, 10.15f, 40], (i, j) => i == 1 && j == 1);
        car.Reset(Vector3.Zero, 0.3f);
        for (var t = 0; t < 240; t++) car.Step(new VehicleInput(1, 0, 0), ground, Dt); // warm-up (JIT)
        var before = GC.GetAllocatedBytesForCurrentThread();
        var contacts = 0;
        for (var t = 0; t < 600; t++)
        {
            car.Step(new VehicleInput(1, 0, 0.3f), ground, Dt);
            contacts += car.WallContacts;
        }
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        log.WriteLine($"{contacts} contacts, {bytes} B allocated");
        Assert.True(contacts > 0);
        Assert.Equal(0, bytes);
    }
}
