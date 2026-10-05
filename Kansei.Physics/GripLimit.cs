using System.Collections.Concurrent;
using System.Numerics;

namespace Kansei.Physics;

/// <summary>
///     What a car can corner at, measured once per spec on flat skid pads: the line pilot holds the circle while the
///     speed creeps up; the highest centripetal acceleration it holds (m/s², 1 s average). The AI plans its corner speeds
///     as a share of it, so every car is driven to its own limit (heavier, softer, front-driven cars lower); the 100 m pad
///     (≈ 120 km/h) shows how much is left at speed, where the steering helpers and weight make heavy cars run wide.
/// </summary>
public static class GripLimit
{
    private const float Dt = 1f / 120;
    private static readonly ConcurrentDictionary<(CarSpec, float), float> Cache = new();

    /// <summary>On the 30 m pad (≈ 75 km/h).</summary>
    public static float Of(CarSpec spec) => At(spec, 30);

    /// <summary>On a pad of <paramref name="radius"/> m.</summary>
    public static float At(CarSpec spec, float radius) => Cache.GetOrAdd((spec, radius), k => Measure(k.Item1, k.Item2));

    private static float Measure(CarSpec spec, float Radius)
    {
        var n = (int)(20 * MathF.Tau * Radius / 2);
        var line = new Vector3[n];
        for (var i = 0; i < n; i++)
        {
            var a = i * 2 / Radius; // left turn: heading +Z, centre at −X… any direction works, the pilot follows it
            line[i] = new Vector3(Radius * (1 - MathF.Cos(a)), 0, Radius * MathF.Sin(a));
        }
        var pilot = new LinePilot(line) { CornerAccel = 100, BrakeDecel = 100, TopSpeed = 100 };
        var car = new Vehicle(spec);
        var ground = new Flat();
        car.Reset(Vector3.Zero, 0);
        pilot.Nearest(car.Position);
        float best = 0, smooth = 0, v = MathF.Sqrt(0.7f * 9.81f * Radius);
        for (var t = 0f; t < 150; t += Dt)
        {
            v += 0.005f * Radius * Dt;
            pilot.SpeedCap = v;
            car.Step(pilot.Drive(car), ground, Dt);
            var (s, lat) = pilot.Track(car.Position);
            if (s > pilot.Length - 50 || MathF.Abs(lat) > 4) break;
            var speed = car.Velocity.Length();
            var r = new Vector2(car.Position.X - Radius, car.Position.Z).Length();
            smooth += (speed * speed / r - smooth) * Dt; // ~1 s average of the centripetal acceleration
            if (t > 10) best = MathF.Max(best, smooth);
        }
        return best;
    }

    private static readonly ConcurrentDictionary<CarSpec, float> Drifts = new();

    /// <summary>
    ///     The fastest drift the car holds on the flat (m/s): from 80 km/h a handbrake flick into a left turn, then full
    ///     throttle with the slip held near 25° by the steering for 4 s; the speed it settles at (drag of the sliding tyres
    ///     against the engine). Faster drift corners cannot be held.
    /// </summary>
    public static float DriftSpeed(CarSpec spec) => Drifts.GetOrAdd(spec, MeasureDrift);

    private static float MeasureDrift(CarSpec spec)
    {
        var car = new Vehicle(spec);
        var ground = new Flat();
        car.Reset(Vector3.Zero, 0);
        for (var i = 0; i < 120 * 60 && car.SpeedKmh < 80; i++) car.Step(new VehicleInput(1, 0, 0), ground, Dt);
        for (var t = 0f; t < 0.4f; t += Dt) car.Step(new VehicleInput(0.5f, 0, -1, Handbrake: true), ground, Dt);
        for (var t = 0f; t < 4; t += Dt) car.Step(new VehicleInput(1, 0, Math.Clamp(3 * (car.SlipAngle - 1.33f * 25 / 57.3f), -1, 1)), ground, Dt);
        return car.SlipAngle > 0.15f ? car.Velocity.Length() : 0; // no drift held: none
    }

    private sealed class Flat : IGround
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

        public int CollideWalls(ReadOnlySpan<Vector3> probes, float radius, Span<WallContact> contacts) => 0;
    }
}
