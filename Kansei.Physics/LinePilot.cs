using System.Numerics;

namespace Kansei.Physics;

/// <summary>
///     Follows a polyline (driving line, world space, XZ is what counts): tracks the car's position along it and
///     drives with pure-pursuit steering and a target speed from the line's curvature (braking-distance look-ahead).
/// </summary>
public sealed class LinePilot
{
    const float G = 9.81f;
    readonly Vector3[] _line;
    readonly float[] _along;    // cumulative XZ distance at each point
    readonly float[] _curvature; // 1/m, XZ
    int _seg;

    /// <summary>Lateral acceleration the pilot plans corners for (m/s²) and its braking deceleration.</summary>
    public float CornerAccel = 0.8f * G;
    public float BrakeDecel = 0.6f * G; // planned below the tyres' limit: braking into a corner shares the friction circle
    public float TopSpeed = 160 / 3.6f;

    /// <summary>Speed the last <see cref="Drive" /> call aimed for (m/s).</summary>
    public float TargetSpeed { get; private set; }

    public LinePilot(Vector3[] line)
    {
        _line = line;
        _along = new float[line.Length];
        for (var i = 1; i < line.Length; i++) _along[i] = _along[i - 1] + Xz(line[i] - line[i - 1]).Length();
        _curvature = new float[line.Length];
        for (var i = 0; i < line.Length; i++)
        {
            // Menger curvature over ±2 points (~10 m spacing on the game's lines)
            Vector2 a = Xz(line[Math.Max(i - 2, 0)]), b = Xz(line[i]), c = Xz(line[Math.Min(i + 2, line.Length - 1)]);
            var den = Vector2.Distance(a, b) * Vector2.Distance(b, c) * Vector2.Distance(a, c);
            _curvature[i] = den < 1e-6f ? 0 : 2 * MathF.Abs(Cross(b - a, c - a)) / den;
        }
    }

    public float Length => _along[^1];

    /// <summary>Nearest segment by global search (resets tracking) — for spawning/reset, O(n).</summary>
    public int Nearest(Vector3 p)
    {
        var best = float.MaxValue;
        for (var i = 0; i < _line.Length - 1; i++)
        {
            var d = DistanceSq(p, i);
            if (d < best) (best, _seg) = (d, i);
        }
        return _seg;
    }

    /// <summary>Distance along the line and signed lateral offset (+ = left of the line direction) of <paramref name="p"/>.</summary>
    public (float Along, float Lateral) Track(Vector3 p)
    {
        // local search around the last segment, allocation-free
        var best = float.MaxValue;
        var from = _seg;
        for (var i = Math.Max(from - 5, 0); i <= Math.Min(from + 10, _line.Length - 2); i++)
        {
            var d = DistanceSq(p, i);
            if (d < best) (best, _seg) = (d, i);
        }
        Vector2 a = Xz(_line[_seg]), ab = Xz(_line[_seg + 1]) - a, ap = Xz(p) - a;
        var t = Math.Clamp(Vector2.Dot(ap, ab) / MathF.Max(ab.LengthSquared(), 1e-6f), 0, 1);
        // left of +dir on XZ with +Y up: Cross2(ab, ap) < 0 means left (x→z rotates the other way, right-handed)
        var lateral = (ap - ab * t).Length() * (Cross(ab, ap) < 0 ? 1 : -1);
        return (_along[_seg] + t * ab.Length(), lateral);
    }

    /// <summary>Point at distance <paramref name="s"/> along the line (clamped).</summary>
    public Vector3 PointAt(float s)
    {
        s = Math.Clamp(s, 0, Length);
        var i = _seg;
        while (i > 0 && _along[i] > s) i--;
        while (i < _line.Length - 2 && _along[i + 1] < s) i++;
        var len = _along[i + 1] - _along[i];
        return Vector3.Lerp(_line[i], _line[i + 1], len < 1e-6f ? 0 : (s - _along[i]) / len);
    }

    /// <summary>Throttle/brake/steer to follow the line. Call once per tick before <see cref="Vehicle.Step" />.</summary>
    public VehicleInput Drive(Vehicle car)
    {
        var (s, _) = Track(car.Position);
        var v = Vector3.Dot(car.Velocity, Vector3.Transform(Vector3.UnitZ, car.Orientation));

        // pure pursuit: arc through the look-ahead point, steer angle = atan(wheelbase · curvature)
        var look = Math.Clamp(5 + 0.5f * v, 8, 30);
        var local = Vector3.Transform(PointAt(s + look) - car.Position, Quaternion.Conjugate(car.Orientation));
        var d2 = MathF.Max(local.X * local.X + local.Z * local.Z, 1);
        var delta = MathF.Atan(car.Spec.Wheelbase * 2 * local.X / d2); // + = left (+X)
        // Vehicle scales the input lock down with speed and adds counter-steer from body slip; undo both
        var assist = v > 2 ? car.Spec.CounterSteerAssist * car.SlipAngle : 0;
        var steer = Math.Clamp((-delta - assist) * (1 + MathF.Abs(v) * car.Spec.SteerSpeedFactor) / car.Spec.MaxSteer, -1, 1);

        // target speed: every point within braking distance must be reachable at its corner speed
        var target = TopSpeed;
        var reach = v * v / (2 * BrakeDecel) + 20;
        for (var i = _seg; i < _line.Length && _along[i] - s < reach; i++)
        {
            var d = MathF.Max(_along[i] - s, 0);
            target = MathF.Min(target, MathF.Sqrt(CornerAccel / MathF.Max(_curvature[i], 1e-4f) + 2 * BrakeDecel * d));
        }
        if (s >= Length - 1) target = 0;

        TargetSpeed = target;
        var err = target - v;
        // traction/stability aid: ease off throttle and brake when the body starts to slide (β 3° … 9°);
        // not at crawling speed, where β is noise and cutting throttle leaves the car parked against a wall
        var calm = v < 8 ? 1 : Math.Clamp(1 - (MathF.Abs(car.SlipAngle) - 0.05f) / 0.1f, 0, 1);
        return new VehicleInput(Math.Clamp(err * 0.25f, 0, 1) * calm, Math.Clamp(-err * 0.5f, 0, 1) * calm, steer);
    }

    float DistanceSq(Vector3 p, int i)
    {
        Vector2 a = Xz(_line[i]), ab = Xz(_line[i + 1]) - a, ap = Xz(p) - a;
        var t = Math.Clamp(Vector2.Dot(ap, ab) / MathF.Max(ab.LengthSquared(), 1e-6f), 0, 1);
        return (ap - ab * t).LengthSquared();
    }

    static Vector2 Xz(Vector3 v) => new(v.X, v.Z);
    static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;
}
