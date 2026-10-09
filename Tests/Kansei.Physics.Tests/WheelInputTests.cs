using System.Numerics;

namespace Kansei.Physics.Tests;

/// <summary>The input a steering wheel adds: direct steering, the clutch pedal, H-shifter jumps over several gears.</summary>
public class WheelInputTests
{
    const float Dt = 1f / 120;
    static readonly FlatGround Flat = new();

    static Vehicle Rolling(float kmh)
    {
        var car = new Vehicle(CarSpec.AE86);
        car.Reset(Vector3.Zero, 0);
        for (var i = 0; i < 120 * 60 && car.SpeedKmh < kmh; i++) car.Step(new VehicleInput(1, 0, 0), Flat, Dt);
        return car;
    }

    [Fact]
    public void DirectSteer_RoadWheelsFollowAtOnce_WithoutSpeedReduction()
    {
        var assisted = Rolling(120);
        var direct = Rolling(120);
        assisted.Step(new VehicleInput(0, 0, 0.5f), Flat, Dt);
        direct.Step(new VehicleInput(0, 0, 0.5f, DirectSteer: true), Flat, Dt);
        Assert.Equal(0.5f * CarSpec.AE86.MaxSteer, direct.Wheels[0].SteerAngle, 4);
        Assert.True(assisted.Wheels[0].SteerAngle < direct.Wheels[0].SteerAngle * 0.5f); // rate-limited and reduced at speed
    }

    [Fact]
    public void ClutchPedalDown_NoDrive()
    {
        var car = new Vehicle(CarSpec.AE86);
        car.Reset(Vector3.Zero, 0);
        for (var t = 0f; t < 2; t += Dt) car.Step(new VehicleInput(1, 0, 0, Clutch: 1), Flat, Dt);
        Assert.True(car.SpeedKmh < 1, $"{car.SpeedKmh} km/h");
        Assert.True(car.Rpm > CarSpec.AE86.IdleRpm + 1000); // revs freely
        for (var t = 0f; t < 2; t += Dt) car.Step(new VehicleInput(1, 0, 0), Flat, Dt);
        Assert.True(car.SpeedKmh > 15, $"{car.SpeedKmh} km/h after letting the clutch out");
    }

    [Fact]
    public void ShiftByMoreThanOne_JumpsGears_HShifter()
    {
        var car = Rolling(30);
        car.AutomaticGearbox = false;
        car.Step(new VehicleInput(0, 0, 0, Shift: 4 - car.Gear), Flat, Dt);
        Assert.Equal(4, car.Gear);
        car.Step(new VehicleInput(0, 0, 0, Shift: -4), Flat, Dt); // to neutral
        Assert.Equal(0, car.Gear);
    }

    /// <summary>A wheel holds a drift by counter-steering (any real road-wheel angle) on throttle; lifting or straight wheels bring the grip back.</summary>
    [Fact]
    public void DirectSteer_DriftHoldsOnCounterSteer()
    {
        float After(float throttle, float counter)
        {
            var car = Rolling(70);
            for (var t = 0f; t < 0.35f; t += Dt) car.Step(new VehicleInput(0.5f, 0, -0.5f, Handbrake: true, DirectSteer: true), Flat, Dt);
            for (var t = 0f; t < 1; t += Dt) car.Step(new VehicleInput(throttle, 0, -counter * MathF.Sign(car.SlipAngle), DirectSteer: true), Flat, Dt);
            return MathF.Abs(car.SlipAngle) * 180 / MathF.PI;
        }
        var (held, lifted, straight) = (After(0.8f, 0.15f), After(0, 0.15f), After(0.8f, 0));
        Assert.True(held > 15, $"{held:F1}°");
        Assert.True(lifted < 3, $"{lifted:F1}°");
        Assert.True(straight < held - 5, $"{straight:F1}° vs {held:F1}°");
    }

    /// <summary>Brake drift: braking into a turn at speed steps the rear out, with any steering device; the same turn without the brake stays gripped.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BrakeIntoTurn_StepsTheRearOut(bool direct)
    {
        float Slip(float brake)
        {
            var car = Rolling(100);
            var steer = direct ? 0.35f : 1f; // a wheel: ~0.2 rad of road wheel, about what a pad asks for at full lock at this speed
            for (var t = 0f; t < 0.5f; t += Dt) car.Step(new VehicleInput(0, brake, steer, DirectSteer: direct), Flat, Dt);
            for (var t = 0f; t < 0.5f; t += Dt) car.Step(new VehicleInput(0.8f, 0, steer, DirectSteer: direct), Flat, Dt);
            return MathF.Abs(car.SlipAngle) * 180 / MathF.PI;
        }
        var (braked, rolled) = (Slip(0.6f), Slip(0));
        Assert.True(braked > rolled + 3, $"braked {braked:F1}° vs {rolled:F1}°");
    }
}
