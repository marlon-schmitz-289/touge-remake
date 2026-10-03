using System.Numerics;
using Kansei.Physics;
using Xunit.Abstractions;

namespace Kansei.Physics.Tests;

/// <summary>Plane y = 0, optionally a wall at z = WallZ facing −Z.</summary>
sealed class FlatGround(float wallZ = float.PositiveInfinity) : IGround
{
    public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out GroundHit hit)
    {
        hit = default;
        if (direction.Y >= 0 || origin.Y < 0) return false;
        var t = -origin.Y / direction.Y;
        if (t > maxDistance) return false;
        hit = new GroundHit(origin + direction * t, Vector3.UnitY, t, 0);
        return true;
    }

    public int CollideWalls(ReadOnlySpan<Vector3> probes, float radius, Span<WallContact> contacts)
    {
        var n = 0;
        for (var i = 0; i < probes.Length && n < contacts.Length; i++)
        {
            var depth = probes[i].Z + radius - wallZ;
            if (depth > 0) contacts[n++] = new WallContact(probes[i] with { Z = wallZ }, -Vector3.UnitZ, depth, i);
        }

        return n;
    }
}

public class VehicleTests(ITestOutputHelper log)
{
    const float Dt = 1f / 120;
    static readonly FlatGround Flat = new();

    static Vehicle NewCar()
    {
        var car = new Vehicle(CarSpec.AE86);
        car.Reset(Vector3.Zero, 0);
        return car;
    }

    static void Run(Vehicle car, VehicleInput input, float seconds, IGround? ground = null)
    {
        for (var t = 0f; t < seconds; t += Dt) car.Step(input, ground ?? Flat, Dt);
    }

    static void AccelerateTo(Vehicle car, float kmh)
    {
        for (var i = 0; i < 120 * 60 && car.SpeedKmh < kmh; i++) car.Step(new VehicleInput(1, 0, 0), Flat, Dt);
    }

    [Fact]
    public void SettlesAtRest()
    {
        var car = NewCar();
        Run(car, default, 3);
        var p = car.Position;
        log.WriteLine($"rest: pos {p}, |v| {car.Velocity.Length():F5}, comp {car.Wheels[0].Compression:F3}/{car.Wheels[2].Compression:F3}");
        Assert.InRange(new Vector2(p.X, p.Z).Length(), 0, 0.01f);
        Assert.InRange(p.Y, 0.40f, 0.56f);
        Assert.InRange(car.Velocity.Length(), 0, 0.01f);
        foreach (var w in car.Wheels) Assert.True(w.Contact);
    }

    [Fact]
    public void ZeroTo100InAe86Range()
    {
        var car = NewCar();
        var t = 0f;
        float t60 = 0;
        while (car.SpeedKmh < 100 && t < 30)
        {
            car.Step(new VehicleInput(1, 0, 0), Flat, Dt);
            t += Dt;
            if (t60 == 0 && car.SpeedKmh >= 60) t60 = t;
        }

        log.WriteLine($"0-60 {t60:F2} s, 0-100 {t:F2} s, gear {car.Gear}, rpm {car.Rpm:F0}");
        Assert.InRange(t, 7.5f, 11f);
        Assert.InRange(MathF.Abs(car.Position.X), 0, 0.5f); // straight line
    }

    [Fact]
    public void SteadyCorneringStaysStable()
    {
        var car = NewCar();
        AccelerateTo(car, 60);
        float maxLat = 0;
        for (var t = 0f; t < 20; t += Dt)
        {
            var v0 = car.Velocity;
            car.Step(new VehicleInput(0.35f, 0, 0.5f), Flat, Dt);
            var a = (car.Velocity - v0) / Dt;
            if (t > 10) maxLat = MathF.Max(maxLat, new Vector2(a.X, a.Z).Length());
        }

        var up = Vector3.Transform(Vector3.UnitY, car.Orientation);
        log.WriteLine($"corner: {car.SpeedKmh:F1} km/h, yaw {car.AngularVelocity.Y:F2} rad/s, lat {maxLat / 9.81f:F2} g, β {car.SlipAngle * 57.3f:F1}°, up.Y {up.Y:F3}");
        Assert.True(float.IsFinite(car.Position.X) && float.IsFinite(car.Velocity.Length()));
        Assert.True(up.Y > 0.95f);
        Assert.InRange(car.SpeedKmh, 20, 120);
        Assert.True(car.AngularVelocity.Y < -0.1f); // steering right = negative yaw about +Y
        Assert.InRange(maxLat / 9.81f, 0.2f, 1.3f);
    }

    [Fact]
    public void HandbrakeTurnYaws()
    {
        var car = NewCar();
        AccelerateTo(car, 80);
        float maxSlip = 0;
        for (var t = 0f; t < 1.5f; t += Dt)
        {
            car.Step(new VehicleInput(0, 0, 1, Handbrake: t < 0.7f), Flat, Dt);
            maxSlip = MathF.Max(maxSlip, MathF.Abs(car.SlipAngle));
        }

        log.WriteLine($"handbrake: max β {maxSlip * 57.3f:F1}°, now {car.SpeedKmh:F1} km/h");
        Assert.True(maxSlip * 57.3f > 15);
    }

    [Fact]
    public void WallStopsCar()
    {
        var wall = new FlatGround(30);
        var car = NewCar();
        Run(car, new VehicleInput(1, 0, 0), 8, wall);
        log.WriteLine($"wall: z {car.Position.Z:F2}, {car.SpeedKmh:F1} km/h");
        Assert.InRange(car.Position.Z, 0, 30 - CarSpec.AE86.Length / 2 + 0.05f);
    }
}
