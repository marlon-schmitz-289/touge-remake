using System.Numerics;
using Xunit.Abstractions;

namespace Kansei.Physics.Tests;

/// <summary>
///     What inputs hold a drift (AI design, PLAN.md "KI-Analyse"): flat ground, pad-style input (no direct steer, the
///     same helpers as a player), handbrake flick into a left turn, then a slip-angle controller on the steering
///     (counter-steer = steer right) and a fixed throttle. Logs the steady state per car/speed/target/throttle.
/// </summary>
public class DriftHoldProbe(ITestOutputHelper log)
{
    const float Dt = 1f / 120, Deg = 180 / MathF.PI;
    static readonly FlatGround Flat = new();

    /// <summary>
    ///     Car at <paramref name="kmh"/>, handbrake <paramref name="hb"/> s with full left lock, then for 3 s:
    ///     steer = <paramref name="kp"/> × (β − target) (+ = right) and throttle <paramref name="throttle"/>.
    ///     Returns mean/std β over the last 2 s (deg), end speed (km/h), path radius (m), spun (|β| &gt; 70°).
    /// </summary>
    public static (float Mean, float Std, float Kmh, float Radius, bool Spun) Hold(CarSpec spec, float kmh, float hb, float target, float throttle, float kp = 2.5f)
    {
        var car = new Vehicle(spec);
        car.Reset(Vector3.Zero, 0);
        for (var i = 0; i < 120 * 60 && car.SpeedKmh < kmh; i++) car.Step(new VehicleInput(1, 0, 0), Flat, Dt);
        for (var t = 0f; t < hb; t += Dt) car.Step(new VehicleInput(0.5f, 0, -1, Handbrake: true), Flat, Dt);
        float sum = 0, sq = 0;
        var n = 0;
        var spun = false;
        Vector3 dir0 = default;
        for (var t = 0f; t < 3; t += Dt)
        {
            var beta = car.SlipAngle; // left turn, tail out: β > 0
            var steer = Math.Clamp(kp * (beta - target), -1, 1);
            car.Step(new VehicleInput(throttle, 0, steer), Flat, Dt);
            spun |= MathF.Abs(car.SlipAngle) > 70 / Deg || car.Velocity.Length() < 3;
            if (t < 1) dir0 = Vector3.Normalize(car.Velocity);
            else
            {
                sum += car.SlipAngle * Deg;
                sq += car.SlipAngle * Deg * car.SlipAngle * Deg;
                n++;
            }
        }
        var mean = sum / n;
        var turn = MathF.Acos(Math.Clamp(Vector3.Dot(dir0, Vector3.Normalize(car.Velocity)), -1, 1)) / 2;
        return (mean, MathF.Sqrt(MathF.Max(sq / n - mean * mean, 0)), car.SpeedKmh, car.Velocity.Length() / MathF.Max(turn, 1e-3f), spun);
    }

    [Fact]
    public void Sweep()
    {
        foreach (var name in new[] { "AE86T", "R34", "EK9" })
        foreach (var kmh in new[] { 50f, 80f })
        foreach (var target in new[] { 15f, 25f, 35f })
        foreach (var throttle in new[] { 0.4f, 0.7f, 1f })
        {
            var r = Hold(CarSpecs.All[name], kmh, 0.3f, target / Deg, throttle);
            log.WriteLine($"{name,-6} {kmh,3:F0} km/h β* {target,2:F0}° thr {throttle:F1}: β {r.Mean,5:F1}±{r.Std,4:F1}°  end {r.Kmh,4:F0} km/h  R {r.Radius,5:F0} m{(r.Spun ? "  SPUN" : "")}");
        }
    }

    [Fact]
    public void Entry_sweep()
    {
        foreach (var name in new[] { "AE86T", "R34", "FD3S", "EK9" })
        foreach (var kmh in new[] { 40f, 55f, 70f })
        foreach (var hb in new[] { 0.3f, 0.5f, 0.7f })
        foreach (var kp in new[] { 1.5f, 3f })
        {
            var r = Hold(CarSpecs.All[name], kmh, hb, 25 / Deg, 0.8f, kp);
            log.WriteLine($"{name,-6} {kmh,3:F0} km/h hb {hb:F1} kp {kp:F1}: β {r.Mean,5:F1}±{r.Std,4:F1}°  end {r.Kmh,4:F0} km/h  R {r.Radius,5:F0} m{(r.Spun ? "  SPUN" : "")}");
        }
    }

    /// <summary>The AE86 holds a 25° drift on throttle with the steering alone (the AI's drift controller relies on it).</summary>
    [Fact]
    public void Ae86_holds_a_steered_drift()
    {
        var r = Hold(CarSpec.AE86, 60, 0.3f, 25 / Deg, 0.7f);
        log.WriteLine($"β {r.Mean:F1}±{r.Std:F1}°, {r.Kmh:F0} km/h, R {r.Radius:F0} m");
        Assert.False(r.Spun);
        Assert.InRange(r.Mean, 12, 35);
    }
}
