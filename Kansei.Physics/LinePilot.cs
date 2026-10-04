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

    /// <summary>Lateral offset (m, + = left of the line) the pursuit point is moved by: overtaking/blocking lines (<see cref="RivalPilot"/>).</summary>
    public float Offset;
    /// <summary>Upper bound on the target speed (m/s), e.g. following a slower car; ∞ = none.</summary>
    public float SpeedCap = float.PositiveInfinity;
    /// <summary>Body slip angle (rad) from which the traction aid starts easing off (fully off 0.1 rad later); drift-style drivers allow more.</summary>
    public float SlipTolerance = 0.05f;

    /// <summary>Speed the last <see cref="Drive" /> call aimed for (m/s).</summary>
    public float TargetSpeed { get; private set; }

    public LinePilot(Vector3[] line)
    {
        _line = line;
        _along = new float[line.Length];
        for (var i = 1; i < line.Length; i++) _along[i] = _along[i - 1] + Xz(line[i] - line[i - 1]).Length();
        _curvature = new float[line.Length];
        _bend = new float[line.Length];
        _wide = new Vector3[line.Length];
        for (var i = 0; i < line.Length; i++)
        {
            // Menger curvature over ±2 points (~10 m spacing on the game's lines)
            Vector2 a = Xz(line[Math.Max(i - 2, 0)]), b = Xz(line[i]), c = Xz(line[Math.Min(i + 2, line.Length - 1)]);
            var den = Vector2.Distance(a, b) * Vector2.Distance(b, c) * Vector2.Distance(a, c);
            var cross = Cross(b - a, c - a);
            _curvature[i] = den < 1e-6f ? 0 : 2 * MathF.Abs(cross) / den;
            _bend[i] = -MathF.Sign(cross) * _curvature[i]; // a left turn (towards +X when heading +Z) has cross < 0
            // outward = left of the travel direction for a right turn (cross > 0), right for a left turn
            var dir = c - a;
            if (dir.LengthSquared() > 1e-6f)
                _wide[i] = new Vector3(dir.Y, 0, -dir.X) / dir.Length() * (MathF.Sign(cross) * MathF.Min(WideMax, WidePerCurvature * _curvature[i]));
        }
    }

    // Pure pursuit cuts corners, and the game's lines run within a metre of some hairpin apexes (IROHA uphill):
    // the pursuit point is moved outward by up to 2 m in tight corners (r 6 m → 2 m, r 100 m → 0.12 m).
    const float WideMax = 3.5f, WidePerCurvature = 20f;
    readonly Vector3[] _wide;
    readonly float[] _bend; // signed curvature, + = left turn

    /// <summary>
    ///     Sharpest bend (signed curvature 1/m, + = left) between <paramref name="from"/> and <paramref name="from"/> + <paramref name="distance"/>
    ///     metres along the line, and how far ahead it is.
    /// </summary>
    public (float Curvature, float At) BendAhead(float from, float distance)
    {
        var i = _seg;
        while (i > 0 && _along[i] > from) i--;
        (float Curvature, float At) best = (0, 0);
        for (; i < _line.Length && _along[i] < from + distance; i++)
            if (_along[i] >= from && MathF.Abs(_bend[i]) > MathF.Abs(best.Curvature)) best = (_bend[i], _along[i] - from);
        return best;
    }

    /// <summary>Unit vector to the left of the line (XZ) at <paramref name="s"/> m along it.</summary>
    public Vector3 LeftAt(float s)
    {
        var d = PointAt(s + 2) - PointAt(s - 2);
        var v = new Vector3(d.Z, 0, -d.X);
        return v.LengthSquared() > 1e-8f ? Vector3.Normalize(v) : Vector3.UnitX;
    }

    public float Length => _along[^1];

    /// <summary>
    ///     Nearest segment by global search (resets tracking) — for spawning/reset, O(n). <paramref name="near"/>: only segments
    ///     within 50 m of that distance along the line — a circuit's line runs two laps, so the start point lies on it twice.
    /// </summary>
    public int Nearest(Vector3 p, float? near = null)
    {
        var best = float.MaxValue;
        for (var i = 0; i < _line.Length - 1; i++)
        {
            if (near is { } s && (_along[i + 1] < s - 50 || _along[i] > s + 50)) continue;
            var d = DistanceSq(p, i);
            if (d < best) (best, _seg) = (d, i);
        }
        return _seg;
    }

    /// <summary>Segment found by the last <see cref="Track" />/<see cref="Nearest" />.</summary>
    public int Segment => _seg;

    /// <summary>
    ///     Puts <paramref name="car" /> at rest on line point <paramref name="i" />, facing along the line, settled on its springs.
    ///     Points without drivable ground under both axles (some lines start on wall faces) or where the body would
    ///     overlap a wall are skipped: first forward, then backward from <paramref name="i" />. Returns the point used, -1 if none.
    /// </summary>
    public int Spawn(Vehicle car, IGround ground, int i)
    {
        i = Math.Clamp(i, 0, _line.Length - 2);
        Span<Vector3> probes = stackalloc Vector3[4];
        Span<WallContact> contacts = stackalloc WallContact[8];
        for (var k = 0; k < 2 * _line.Length; k++)
        {
            var at = k < _line.Length - 1 - i ? i + k : i - (k - (_line.Length - 1 - i)) - 1; // i, i+1, …, end, then i-1, i-2, …
            if (at < 0) return -1;
            var axle = Vector3.Normalize(_line[at + 1] - _line[at]) * 3;
            if (!ground.Raycast(_line[at] + Vector3.UnitY * 5, -Vector3.UnitY, 20, out var hit)
                || !ground.Raycast(_line[at] - axle + Vector3.UnitY * 5, -Vector3.UnitY, 20, out _)
                || !ground.Raycast(_line[at] + axle + Vector3.UnitY * 5, -Vector3.UnitY, 20, out _)) continue;
            car.Reset(hit.Point, MathF.Atan2(axle.X, axle.Z));
            car.WallProbes(probes);
            if (ground.CollideWalls(probes, Vehicle.ProbeRadius, contacts) > 0) continue;
            for (var t = 0; t < 60; t++) car.Step(new VehicleInput(0, 0, 0, Handbrake: true), ground, 1f / 120); // handbrake: brake at standstill engages reverse
            Nearest(car.Position, _along[at]);
            return at;
        }
        return -1;
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
    public Vector3 PointAt(float s) => At(_line, s);

    Vector3 At(Vector3[] values, float s)
    {
        s = Math.Clamp(s, 0, Length);
        var i = _seg;
        while (i > 0 && _along[i] > s) i--;
        while (i < _line.Length - 2 && _along[i + 1] < s) i++;
        var len = _along[i + 1] - _along[i];
        return Vector3.Lerp(values[i], values[i + 1], len < 1e-6f ? 0 : (s - _along[i]) / len);
    }

    /// <summary>Throttle/brake/steer to follow the line. Call once per tick before <see cref="Vehicle.Step" />.</summary>
    public VehicleInput Drive(Vehicle car)
    {
        var (s, _) = Track(car.Position);
        var v = Vector3.Dot(car.Velocity, Vector3.Transform(Vector3.UnitZ, car.Orientation));

        // pure pursuit: arc through the look-ahead point, steer angle = atan(wheelbase · curvature)
        var look = Math.Clamp(5 + 0.5f * v, 8, 30);
        var aim = PointAt(s + look) + At(_wide, s + look) + (Offset != 0 ? LeftAt(s + look) * Offset : Vector3.Zero);
        var local = Vector3.Transform(aim - car.Position, Quaternion.Conjugate(car.Orientation));
        var d2 = MathF.Max(local.X * local.X + local.Z * local.Z, 1);
        var delta = MathF.Atan(car.Spec.Wheelbase * 2 * local.X / d2); // + = left (+X)
        // Vehicle scales the input lock down with speed and adds counter-steer from body slip; undo both
        var assist = v > 2 ? car.Spec.CounterSteerAssist * car.SlipAngle : 0;
        var steer = Math.Clamp((-delta - assist) * (1 + MathF.Abs(v) * car.Spec.SteerSpeedFactor) / car.Spec.MaxSteer, -1, 1);

        // target speed: every point within braking distance must be reachable at its corner speed
        var target = MathF.Min(TopSpeed, SpeedCap);
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
        var calm = v < 8 ? 1 : Math.Clamp(1 - (MathF.Abs(car.SlipAngle) - SlipTolerance) / 0.1f, 0, 1);
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
