using System.Numerics;
using Xunit.Abstractions;

namespace Kansei.Physics.Tests;

public class CarCollisionTests(ITestOutputHelper log)
{
    const float Dt = 1f / 120;
    static readonly FlatGround Flat = new();

    static Vehicle Car(Vector3 at, float heading = 0, CarSpec? spec = null)
    {
        var car = new Vehicle(spec ?? CarSpec.AE86);
        car.Reset(at, heading);
        for (var i = 0; i < 60; i++) car.Step(new VehicleInput(0, 0, 0, Handbrake: true), Flat, Dt); // settle
        return car;
    }

    /// <summary>Gives the body velocity <paramref name="v"/> through its centre (no spin).</summary>
    static void Push(Vehicle car, Vector3 v) => car.ApplyImpulseAt(car.Position, (v - car.Velocity) * car.Spec.Mass);

    static bool Overlapping(Vehicle a, Vehicle b) => CarCollision.Overlap(CarCollision.BoxOf(a), CarCollision.BoxOf(b), out _, out _);

    [Fact]
    public void Sat_finds_the_axis_of_least_overlap_pointing_from_a_to_b()
    {
        var a = Car(Vector3.Zero);
        var b = Car(new Vector3(1.5f, 0, 0.3f)); // side by side, 1.5 m apart, bodies 1.63 m wide: 0.13 m overlap sideways
        Assert.True(CarCollision.Overlap(CarCollision.BoxOf(a), CarCollision.BoxOf(b), out var n, out var depth));
        Assert.True(n.X > 0.99f);
        Assert.InRange(depth, 0.1f, 0.16f);
        var c = Car(new Vector3(1.7f, 0, 0));
        Assert.False(Overlapping(a, c));
        var above = new Vehicle(CarSpec.AE86); // a car on a bridge above
        above.Reset(new Vector3(0, 3, 0), 0);
        Assert.False(Overlapping(a, above));
    }

    [Fact]
    public void Contact_conserves_momentum_and_separates()
    {
        var a = Car(Vector3.Zero);
        var b = Car(new Vector3(0, 0, 4.0f), 0, CarSpecs.All["FD3S"]); // 0.2 m into a's front
        Push(a, new Vector3(0, 0, 12));
        var p0 = a.Velocity * a.Spec.Mass + b.Velocity * b.Spec.Mass;
        var hit = CarCollision.Resolve(a, b, a.Position, a.Orientation, b.Position, b.Orientation);
        Assert.NotNull(hit);
        var p1 = a.Velocity * a.Spec.Mass + b.Velocity * b.Spec.Mass;
        log.WriteLine($"impact {hit.Value.ImpactSpeed:F1} m/s, depth {hit.Value.Depth:F2} m, a {a.Velocity.Z:F2} b {b.Velocity.Z:F2} m/s");
        Assert.InRange((p1 - p0).Length(), 0, 1e-2f * p0.Length());
        Assert.InRange(hit.Value.ImpactSpeed, 11.9f, 12.1f);
        // inelastic bumpers: b now faster than a, closing speed reversed by the restitution only
        Assert.True(b.Velocity.Z > a.Velocity.Z);
        Assert.InRange(b.Velocity.Z - a.Velocity.Z, 0, 12 * CarCollision.Restitution + 0.1f);
        Assert.False(Overlapping(a, b));
    }

    [Fact]
    public void Fast_side_hit_within_one_tick_does_not_tunnel()
    {
        // b crosses a's whole width in one tick (300 km/h sideways, 0.7 m per tick at 120 Hz; posed 3 m through): the sweep
        // still finds the contact on the side it came from and puts it back there
        var a = Car(Vector3.Zero);
        var b = Car(new Vector3(-2f, 0, 0));
        var prevB = b.Position;
        b.Translate(new Vector3(3.2f, 0, 0)); // now 1.2 m right of a's centre: overlapping from the far side
        Push(b, new Vector3(83, 0, 0));
        var hit = CarCollision.Resolve(a, b, a.Position, a.Orientation, prevB, b.Orientation);
        Assert.NotNull(hit);
        Assert.True(hit.Value.Normal.X < -0.99f, $"normal {hit.Value.Normal}"); // from a towards where b came from
        Assert.True(b.Position.X < a.Position.X, "b is back on the side it came from");
        Assert.False(Overlapping(a, b));
        Assert.True(b.Velocity.X < a.Velocity.X + 1e-3f, "no longer closing");
    }

    [Fact]
    public void Rear_end_on_the_road_pushes_the_car_ahead_without_overlap()
    {
        var a = Car(Vector3.Zero);
        var b = Car(new Vector3(0, 0, -12));
        Push(b, new Vector3(0, 0, 15));
        float maxDepth = 0;
        var touched = false;
        for (var t = 0f; t < 3; t += Dt)
        {
            Vector3 pa = a.Position, pb = b.Position;
            Quaternion ra = a.Orientation, rb = b.Orientation;
            a.Step(new VehicleInput(0, 0, 0), Flat, Dt);
            b.Step(new VehicleInput(0, 0, 0), Flat, Dt);
            if (CarCollision.Resolve(a, b, pa, ra, pb, rb) is { } c) (touched, maxDepth) = (true, MathF.Max(maxDepth, c.Depth));
            Assert.False(Overlapping(a, b));
        }
        log.WriteLine($"a {a.Velocity.Z:F2} m/s at z {a.Position.Z:F2}, b {b.Velocity.Z:F2} m/s, max pre-correction depth {maxDepth * 100:F1} cm");
        Assert.True(touched);
        Assert.True(a.Position.Z > 1, "the car ahead was shoved forward");
        Assert.InRange(maxDepth, 0, 0.3f);
    }

    [Fact]
    public void Off_centre_side_hit_spins_the_car()
    {
        // b's nose hits a's rear quarter from the side (a PIT): a yaws
        var a = Car(Vector3.Zero);
        var b = Car(new Vector3(-2.2f, 0, -1.6f), MathF.PI / 2); // facing +X, nose at a's left rear
        Push(b, new Vector3(10, 0, 0));
        var hit = CarCollision.Resolve(a, b, a.Position, a.Orientation, b.Position - new Vector3(0.2f, 0, 0), b.Orientation);
        Assert.NotNull(hit);
        log.WriteLine($"a yaw rate {a.AngularVelocity.Y:F3} rad/s, a {a.Velocity}");
        Assert.True(MathF.Abs(a.AngularVelocity.Y) > 0.2f);
        Assert.InRange(MathF.Abs(a.AngularVelocity.X) + MathF.Abs(a.AngularVelocity.Z), 0, 0.05f); // no roll/pitch kick
    }
}
