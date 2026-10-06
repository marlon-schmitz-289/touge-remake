using System.Numerics;
using Xunit.Abstractions;

namespace Kansei.Physics.Tests;

public class CarSpecsTests(ITestOutputHelper log)
{
    const float Dt = 1f / 120;

    /// <summary>
    ///     Every car settles on its springs and runs 0–100 km/h straight in a time that fits its power-to-weight class:
    ///     roughly 1.2 s per kg/PS (AE86 ≈ 7.2 kg/PS → ~9 s, R32 5.1 → ~6 s, Cappuccino 10.9 → ~13 s), ±40 %.
    /// </summary>
    [Fact]
    public void Every_car_settles_and_accelerates_for_its_class()
    {
        Assert.Equal(32, CarSpecs.All.Count);
        foreach (var (name, spec) in CarSpecs.All)
        {
            var car = new Vehicle(spec);
            car.Reset(Vector3.Zero, 0);
            for (var t = 0f; t < 3; t += Dt) car.Step(default, new FlatGround(), Dt);
            Assert.InRange(car.Position.Y, spec.CogHeight - 0.1f, spec.CogHeight + 0.1f);
            Assert.InRange(car.Velocity.Length(), 0, 0.02f);

            var time = 0f;
            while (car.SpeedKmh < 100 && time < 30)
            {
                car.Step(new VehicleInput(1, 0, 0), new FlatGround(), Dt);
                time += Dt;
            }
            var ps = spec.TorqueNm.Zip(spec.TorqueRpm, (nm, rpm) => nm * rpm * MathF.PI / 30 / 735.5f).Max();
            var kgPerPs = spec.Mass / ps;
            log.WriteLine($"{name,-6} {spec.Mass,5:F0} kg {kgPerPs,5:F1} kg/PS  front {spec.DriveFront:F2}  0-100 {time,5:F2} s  gear {car.Gear}  |x| {MathF.Abs(car.Position.X):F2} m");
            Assert.InRange(time, 0.6f * 1.2f * kgPerPs, 1.4f * 1.2f * kgPerPs);
            Assert.InRange(MathF.Abs(car.Position.X), 0, 0.5f);
        }
    }

    /// <summary>
    ///     Feel per car (keyboard steer ramp 3/s): steer-in at 110 km/h with full throttle for 2 s, then let go — every car
    ///     slides less than a spin and recovers; logs how far each one rotates (FR/MR most, 4WD/FF less).
    /// </summary>
    [Fact]
    public void Every_car_drifts_and_recovers()
    {
        foreach (var (name, spec) in CarSpecs.All)
        {
            var car = new Vehicle(spec);
            car.Reset(Vector3.Zero, 0);
            for (var i = 0; i < 120 * 60 && car.SpeedKmh < 110; i++) car.Step(new VehicleInput(1, 0, 0), new FlatGround(), Dt);
            float s = 0, maxBeta = 0;
            for (var t = 0f; t < 5; t += Dt)
            {
                s += Math.Clamp((t < 2 ? 1 : 0) - s, -3 * Dt, 3 * Dt);
                car.Step(new VehicleInput(t < 2 ? 1 : 0.4f, 0, s), new FlatGround(), Dt);
                if (t < 2) maxBeta = MathF.Max(maxBeta, MathF.Abs(car.SlipAngle) * 57.3f);
            }
            log.WriteLine($"{name,-6} front {spec.DriveFront:F2}: steer-in @110 max β {maxBeta,4:F0}°, after release β {car.SlipAngle * 57.3f,4:F0}°, {car.SpeedKmh:F0} km/h");
            Assert.True(maxBeta < 50, $"{name} spun");
            Assert.True(MathF.Abs(car.SlipAngle * 57.3f) < 15, $"{name} did not recover");
        }
    }

    /// <summary>
    ///     BoP: the driven cars are the real ones with only the torque scaled by the car's factor (weight, grip, gearing,
    ///     drift layer untouched), every car has a factor, and the power-to-weight spread shrinks (stock 4.5 kg/PS Impreza … 11.1 AE85, 10.9
    ///     Cappuccino; balanced 4.7 S2000 … 7.9 Cappuccino: light cars keep a little less power per kg, they gain it back in the bends).
    /// </summary>
    [Fact]
    public void Bop_scales_only_the_torque()
    {
        Assert.Equal(CarSpecs.Real.Keys.Order(), CarSpecs.Bop.Keys.Order());
        static float KgPerPs(CarSpec s) => s.Mass / s.TorqueNm.Zip(s.TorqueRpm, (nm, rpm) => nm * rpm * MathF.PI / 30 / 735.5f).Max();
        foreach (var (name, real) in CarSpecs.Real)
        {
            var b = CarSpecs.All[name];
            var k = CarSpecs.Bop[name];
            Assert.InRange(k, 0.6f, 1.7f);
            Assert.Equal(real.TorqueNm.Select(t => t * k), b.TorqueNm);
            Assert.Equal(real with { TorqueNm = b.TorqueNm }, b); // nothing else differs
        }
        float Spread(Func<CarSpec, float> f, IEnumerable<CarSpec> cars) => cars.Max(f) / cars.Min(f);
        Assert.True(Spread(KgPerPs, CarSpecs.All.Values) < 0.7f * Spread(KgPerPs, CarSpecs.Real.Values));
        Assert.Same(CarSpecs.Real["AE86T"], CarSpecs.Real["AE86L"]);
        Assert.Equal(CarSpec.AE86, CarSpecs.Real["AE86T"]);
    }
}
