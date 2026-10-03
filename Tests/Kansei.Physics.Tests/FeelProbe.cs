using System.Numerics;
using Kansei.Physics;
using Xunit.Abstractions;

namespace Kansei.Physics.Tests;

/// <summary>
///     Keyboard-style manoeuvres for handling tuning: corner at speed, handbrake flick, steer-in drift.
///     Logs numbers; the asserts encode the arcade feel target (Initial D: 140 km/h corners, easy controllable drifts).
/// </summary>
public class FeelProbe(ITestOutputHelper log)
{
    const float Dt = 1f / 120, Deg = 180 / MathF.PI;
    static readonly FlatGround Flat = new();

    static Vehicle CarAt(float kmh)
    {
        var car = new Vehicle(CarSpec.AE86);
        car.Reset(Vector3.Zero, 0);
        for (var i = 0; i < 120 * 60 && car.SpeedKmh < kmh; i++) car.Step(new VehicleInput(1, 0, 0), Flat, Dt);
        return car;
    }

    /// <summary>Steer like a keyboard: ramp 3/s toward target. Throttle holds <paramref name="holdKmh"/> (P-controller) unless given.</summary>
    static (float maxBeta, float yawRate, float latG, float kmh) Drive(Vehicle car, float seconds, float steer, float? throttle = null,
        bool handbrake = false, float holdKmh = 0)
    {
        float s = 0, maxBeta = 0;
        for (var t = 0f; t < seconds; t += Dt)
        {
            s += Math.Clamp(steer - s, -3 * Dt, 3 * Dt);
            var th = throttle ?? Math.Clamp((holdKmh - car.SpeedKmh) * 0.2f, 0, 0.7f); // part throttle = grip driving
            car.Step(new VehicleInput(th, 0, s, handbrake), Flat, Dt);
            maxBeta = MathF.Max(maxBeta, MathF.Abs(car.SlipAngle * Deg));
        }
        var v = car.Velocity.Length();
        return (maxBeta, car.AngularVelocity.Y, MathF.Abs(car.AngularVelocity.Y) * v / 9.81f, car.SpeedKmh);
    }

    [Theory]
    [InlineData(80)]
    [InlineData(110)]
    [InlineData(140)]
    public void Corner_full_keyboard_lock(float kmh)
    {
        var car = CarAt(kmh);
        var (beta, yaw, latG, v) = Drive(car, 3, 1, holdKmh: kmh);
        var radius = car.Velocity.Length() / MathF.Max(MathF.Abs(yaw), 1e-3f);
        log.WriteLine($"{kmh} km/h full lock, part throttle: radius {radius:F0} m, lat {latG:F2} g, max β {beta:F0}°, end {v:F0} km/h");
        Assert.True(beta < 60, "spun out");
        Assert.True(latG > 1.2f, $"too little grip: {latG:F2} g");
    }

    [Fact]
    public void Handbrake_flick_is_controllable()
    {
        var car = CarAt(80);
        Drive(car, 0.35f, 0.6f, 0.3f, handbrake: true);
        var (beta, _, _, v) = Drive(car, 3, 0, 0.5f);
        log.WriteLine($"handbrake flick @80: max β {beta:F0}°, end β {car.SlipAngle * Deg:F0}°, end {v:F0} km/h");
        Assert.InRange(beta, 10, 60);
        Assert.True(MathF.Abs(car.SlipAngle * Deg) < 15, "did not recover");
    }

    /// <summary>Held drift (full throttle, lock held) for 4 s: arcade keeps the momentum.</summary>
    [Fact]
    public void Held_drift_keeps_speed()
    {
        var car = CarAt(100);
        var (beta, _, _, v) = Drive(car, 4, 1, 1);
        log.WriteLine($"held drift 4 s @100: max β {beta:F0}°, end β {car.SlipAngle * Deg:F0}°, 100→{v:F0} km/h");
        Assert.True(beta > 10, "no drift");
        Assert.True(v > 90, $"lost too much speed: {v:F0} km/h");
    }

    [Fact]
    public void Steer_in_drift_at_speed()
    {
        var car = CarAt(110);
        var (beta, _, _, v) = Drive(car, 2, 1, 1);
        var hold = car.SlipAngle * Deg;
        var (_, _, _, v2) = Drive(car, 3, 0, 0.4f);
        log.WriteLine($"steer-in @110: max β {beta:F0}°, β after 2 s {hold:F0}°, end β {car.SlipAngle * Deg:F0}°, {v:F0}→{v2:F0} km/h");
        Assert.InRange(beta, 10, 50);
        Assert.True(MathF.Abs(car.SlipAngle * Deg) < 15, "did not recover");
    }
}
