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
        Assert.InRange(p.Y, CarSpec.AE86.CogHeight - 0.08f, CarSpec.AE86.CogHeight + 0.08f); // settles near the spec CoG height
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
        Assert.InRange(maxLat / 9.81f, 0.2f, 1.7f); // arcade grip (μ 1.7), not a road tyre
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

    [Fact]
    public void AutomaticReversesOnBrakeAtStandstill()
    {
        var car = NewCar();
        Run(car, new VehicleInput(0, 1, 0), 3);
        var back = Vector3.Dot(car.Velocity, Vector3.Transform(Vector3.UnitZ, car.Orientation));
        var up = Vector3.Transform(Vector3.UnitY, car.Orientation);
        log.WriteLine($"reverse: gear {car.Gear}, {back * 3.6f:F1} km/h, heading z {Vector3.Transform(Vector3.UnitZ, car.Orientation).Z:F2}");
        Assert.Equal(-1, car.Gear);
        Assert.True(back < -2, "not driving backwards");
        Assert.True(Vector3.Transform(Vector3.UnitZ, car.Orientation).Z > 0.95f && up.Y > 0.95f, "turned around while reversing");

        Run(car, new VehicleInput(1, 0, 0), 4);
        var fwd = Vector3.Dot(car.Velocity, Vector3.Transform(Vector3.UnitZ, car.Orientation));
        Assert.True(car.Gear >= 1 && fwd > 2, $"did not switch back to forward: gear {car.Gear}, {fwd * 3.6f:F1} km/h");
    }

    [Fact]
    public void AutomaticDoesNotUpshiftOnHandbrakeRevs()
    {
        var car = NewCar();
        AccelerateTo(car, 50);
        var gear = car.Gear;
        Run(car, new VehicleInput(1, 0, 0.5f, Handbrake: true), 1.5f);
        log.WriteLine($"handbrake revs: gear {gear} -> {car.Gear}, rpm {car.Rpm:F0}");
        Assert.True(car.Gear <= gear, $"upshifted on free revs: {gear} -> {car.Gear}");
    }

    /// <summary>Full throttle lugging in top gear (above the plain downshift revs) kicks down to the gear that pulls harder.</summary>
    [Fact]
    public void AutomaticKicksDownAtFullThrottle()
    {
        var car = NewCar();
        AccelerateTo(car, 100);
        while (car.Gear < car.Spec.Gears.Length) Run(car, new VehicleInput(0, 0, 0, Shift: 1), 0.6f);
        Assert.True(car.Rpm > car.Spec.AutoDownRpm, $"{car.Rpm:F0} rpm: the plain downshift would do it");
        Run(car, new VehicleInput(1, 0, 0), 1);
        log.WriteLine($"kickdown: gear {car.Gear}, {car.Rpm:F0} rpm, {car.SpeedKmh:F0} km/h");
        Assert.True(car.Gear < car.Spec.Gears.Length, $"stayed in {car.Gear} at {car.Rpm:F0} rpm");
    }

    /// <summary>
    ///     Engine speed is a flywheel behind a friction clutch: through an upshift at full throttle it falls over ≥ 0.1 s to
    ///     the new gear's revs, through a manual downshift without blip it is dragged up — never a jump (limiter aside).
    /// </summary>
    [Fact]
    public void RpmIsContinuousThroughShifts()
    {
        var car = NewCar();
        float maxStep = 0, prev = car.Rpm, shiftAt = -1, before = 0, t = 0;
        void Tick(VehicleInput input)
        {
            car.Step(input, Flat, Dt);
            t += Dt;
            if (prev < car.Spec.RevLimit - 50 && car.Rpm < car.Spec.RevLimit - 50) maxStep = MathF.Max(maxStep, MathF.Abs(car.Rpm - prev));
            prev = car.Rpm;
        }

        while (car.Gear == 1 && t < 10) { before = car.Rpm; Tick(new VehicleInput(1, 0, 0)); }
        shiftAt = t;
        var coupled = before * car.Spec.Gears[1] / car.Spec.Gears[0];
        while (car.Rpm > coupled + 200 && t < shiftAt + 1) Tick(new VehicleInput(1, 0, 0));
        var drop = t - shiftAt;
        log.WriteLine($"1→2 at {before:F0} rpm: {drop:F2} s down to {car.Rpm:F0} (coupled ≈ {coupled:F0}), max step {maxStep:F0} rpm/tick");
        Assert.InRange(drop, 0.1f, 0.6f);

        car.AutomaticGearbox = false;
        while (car.Gear < 3 && t < 30) Tick(new VehicleInput(1, 0, 0, Shift: car.Rpm > 7000 ? 1 : 0));
        for (var i = 0; i < 60; i++) Tick(new VehicleInput(0, 0, 0));
        var low = car.Rpm;
        Tick(new VehicleInput(0, 0, 0, Shift: -1));
        for (var i = 0; i < 90; i++) Tick(new VehicleInput(0, 0, 0));
        log.WriteLine($"3→2 lifted: {low:F0} → {car.Rpm:F0} rpm, max step {maxStep:F0} rpm/tick");
        Assert.InRange(car.Rpm, low * 1.25f, low * 1.6f); // ratio 1.46, minus the speed lost to engine braking
        Assert.InRange(maxStep, 1, 250);
    }
}
