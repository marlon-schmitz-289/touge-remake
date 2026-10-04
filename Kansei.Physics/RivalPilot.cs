using System.Numerics;

namespace Kansei.Physics;

/// <summary>
///     How an AI rival drives, each 0..1: <paramref name="Skill"/> = planned cornering/braking grip (pace, braking points),
///     <paramref name="Aggression"/> = how readily it dives for a gap, how close it follows and how hard it covers the inside
///     against a car behind, <paramref name="Drift"/> = drift style (handbrake flick into hairpins, holds more body slip
///     before the traction aid eases off).
/// </summary>
public readonly record struct RivalStyle(float Skill, float Aggression, float Drift);

/// <summary>Another car as an AI sees it: metres along and lateral (+ left) on the same driving line, forward speed (m/s).</summary>
public readonly record struct Opponent(float Along, float Lateral, float Speed);

/// <summary>
///     AI opponent on a <see cref="LinePilot"/> (the course's racing line, CRS_DRV _I/_O): braking points come from the
///     line's curvature at the skill's planned grip (<see cref="Pace"/>), traffic from the other cars on the same line —
///     following (speed capped to the car ahead at a gap by speed), passing (offset alongside on the inside of the next
///     bend, or the side the other car leaves open; only where the road is wide enough), blocking (covering the lateral
///     of a car close behind, by aggression). <see cref="RubberBand"/> moves the pace ±5 % (subtle catch-up).
/// </summary>
public sealed class RivalPilot
{
    const float G = 9.81f;

    /// <summary>Lateral centre distance aimed for when passing alongside (car width + elbow room).</summary>
    public const float PassGap = 2.4f;

    /// <summary>Lateral offset at most: 2.6 m on straights, 0.3 m in a hairpin (radius ≤ 30 m: the line runs along the edges there).</summary>
    public const float MaxOffsetStraight = 2.6f, MaxOffsetBend = 0.3f;

    /// <summary>Distance kept between the car's centre line and the end of the drivable ground (half a body plus elbow room).</summary>
    public const float EdgeMargin = 1.3f;

    /// <summary>Lateral centre distance from which two cars are side by side (body widths ~1.7 m): no more speed cap behind.</summary>
    public const float Alongside = 1.8f;

    public enum Mode { Line, Follow, Pass, Block }

    float _flickUntil, _flickDoneAt = float.MinValue, _time;
    int _passSide;
    bool _fresh = true; // first tick after start/reset: the offset starts where the car stands (side-by-side grid)

    public RivalPilot(Vector3[] line, RivalStyle style)
    {
        Pilot = new LinePilot(line) { TopSpeed = 250 / 3.6f };
        Style = style;
    }

    /// <summary>Own line follower (tracking state per car).</summary>
    public LinePilot Pilot { get; }
    public RivalStyle Style { get; set; }
    /// <summary>−1 … +1 from the race: + = behind the player, push (≤ +5 % pace); − = ahead, ease off. 0 = off.</summary>
    public float RubberBand { get; set; }
    /// <summary>Current lateral target (m, + left), eased.</summary>
    public float Offset { get; private set; }
    public Mode State { get; private set; }

    /// <summary>Planned cornering and braking deceleration (m/s²) for <paramref name="skill"/>: 0.68–0.86 g and 0.50–0.64 g.</summary>
    public static (float Corner, float Brake) Pace(float skill) => (G * (0.68f + 0.18f * skill), G * (0.50f + 0.14f * skill));

    /// <summary>Following distance (m, centre to centre) behind a car at <paramref name="speed"/> m/s.</summary>
    public static float FollowGap(float speed, float aggression) => 5.5f + speed * (0.35f - 0.2f * aggression);

    /// <summary>Closest centre distance behind a car while pulling out to pass it: a car length and a little, more with speed.</summary>
    public static float PassFollowGap(float speed, float aggression) => 4.8f + speed * (0.1f - 0.04f * aggression);

    /// <summary>Lateral room at <paramref name="along"/>: less near a tight bend.</summary>
    public float MaxOffset(float along)
    {
        var (k, _) = Pilot.BendAhead(along - 10, 30);
        return float.Lerp(MaxOffsetStraight, MaxOffsetBend, Math.Clamp(MathF.Abs(k) * 30, 0, 1));
    }

    /// <summary>
    ///     Drivable full-grip ground either side of the line at <paramref name="along"/>: metres to the left / right (0.5 m
    ///     steps, at most 4 m) before the ground ends (wall, drop) or turns to grass.
    /// </summary>
    public (float Left, float Right) Room(Vehicle car, IGround ground, float along)
    {
        var p = Pilot.PointAt(along) + Vector3.UnitY * 3;
        var left = Pilot.LeftAt(along);
        float Side(float sign)
        {
            for (var k = 1; k <= 8; k++)
                if (!ground.Raycast(p + left * (sign * 0.5f * k), -Vector3.UnitY, 8, out var hit) || (car.SurfaceGrip?.Invoke(hit.Surface) ?? 1) < 0.9f)
                    return 0.5f * (k - 1);
            return 4;
        }
        return (Side(1), Side(-1));
    }

    /// <summary>Input for this tick; <paramref name="others"/> = every other car on the course.</summary>
    public VehicleInput Drive(Vehicle car, IGround ground, ReadOnlySpan<Opponent> others, float dt)
    {
        _time += dt;
        var (s, lat) = Pilot.Track(car.Position);
        if (_fresh) (Offset, _fresh) = (lat, false);
        var v = Vector3.Dot(car.Velocity, Vector3.Transform(Vector3.UnitZ, car.Orientation));
        var (corner, brake) = Pace(Style.Skill);
        var pace = 1 + 0.05f * Math.Clamp(RubberBand, -1, 1);
        (Pilot.CornerAccel, Pilot.BrakeDecel, Pilot.SlipTolerance) = (corner * pace, brake * pace, 0.05f + 0.1f * Style.Drift);

        // nearest car ahead (50 m) and behind (20 m)
        int ahead = -1, behind = -1;
        float dsAhead = float.MaxValue, dsBehind = float.MinValue;
        for (var i = 0; i < others.Length; i++)
        {
            var ds = others[i].Along - s;
            if (ds > 0 && ds < 50 && ds < dsAhead) (ahead, dsAhead) = (i, ds);
            else if (ds <= 0 && ds > -20 && ds > dsBehind) (behind, dsBehind) = (i, ds);
        }

        // lateral limits: the road's width here and over the next 25 m (body half width + margin inside), less in tight bends
        float lo = -MaxOffset(s), hi = -lo;
        if (ahead >= 0 || behind >= 0 || Offset != 0)
            foreach (var d in (ReadOnlySpan<float>)[0f, 12f, 25f])
            {
                var (l, r) = Room(car, ground, s + d);
                (lo, hi) = (MathF.Max(lo, -(r - EdgeMargin)), MathF.Min(hi, l - EdgeMargin));
            }
        if (lo > hi) lo = hi = (lo + hi) / 2;

        float target = 0, cap = float.PositiveInfinity;
        State = Mode.Line;
        if (ahead < 0 || dsAhead > 20) _passSide = 0; // passed, or the car ahead got away
        if (ahead >= 0)
        {
            var o = others[ahead];
            var closing = v - o.Speed;
            if (_passSide == 0 && dsAhead < 20 && (closing > 1 - Style.Aggression || dsAhead < 8 + 6 * Style.Aggression))
            {
                // the inside of the next bend (a late-braking dive), else the side the other car leaves open; a side
                // without room for the car alongside is no option
                var (k, at) = Pilot.BendAhead(s, 60);
                var side = MathF.Abs(k) > 1 / 80f && at > dsAhead && Style.Aggression > 0.3f ? MathF.Sign(k) : o.Lateral > lat ? -1 : 1;
                bool Fits(int sd) { var x = o.Lateral + sd * PassGap; return x >= lo - 0.3f && x <= hi + 0.3f; }
                _passSide = Fits((int)side) ? (int)side : Fits(-(int)side) ? -(int)side : 0;
            }
            if (_passSide != 0)
            {
                target = o.Lateral + _passSide * PassGap;
                State = Mode.Pass;
            }
            // not yet alongside: keep out of its boot (no punting) — while pulling out to pass only a car length plus a
            // little; alongside, drive past
            if (MathF.Abs(lat - o.Lateral) < Alongside)
            {
                // pulling out only counts where the road leaves room to get alongside; else it is following
                var pulling = _passSide != 0 && MathF.Abs(Math.Clamp(target, lo, hi) - o.Lateral) >= Alongside;
                var gap = pulling ? PassFollowGap(o.Speed, Style.Aggression) : FollowGap(o.Speed, Style.Aggression);
                cap = MathF.Max(o.Speed + 1.2f * (dsAhead - gap), 0);
                if (cap < v && State == Mode.Line) State = Mode.Follow;
            }
        }
        if (State == Mode.Line && behind >= 0 && Style.Aggression > 0.25f && dsBehind is > -15 and < -4)
        {
            // defend the inside before a bend when the car behind is coming up on that side — once, and never
            // against a car already alongside (no chopping); on the straights it may pass
            var (k, at) = Pilot.BendAhead(s, 70);
            var attacker = others[behind].Lateral;
            if (MathF.Abs(k) > 1 / 80f && at > 15 && MathF.Sign(attacker - lat) == MathF.Sign(k))
            {
                target = MathF.Sign(k) * 1.2f * Style.Aggression;
                State = Mode.Block;
            }
        }
        // a car alongside (overlapping lengthwise): stay on our side of it instead of steering back onto the line through it
        foreach (var o in others)
            if (MathF.Abs(o.Along - s) < 6 && MathF.Abs(o.Lateral - lat) < PassGap + 1)
                target = o.Lateral > lat ? MathF.Min(target, o.Lateral - PassGap) : MathF.Max(target, o.Lateral + PassGap);
        target = Math.Clamp(target, lo, hi);
        // eased; faster when the road narrows under it
        var rate = (Offset < lo - 0.05f || Offset > hi + 0.05f ? 4 : State == Mode.Pass ? 2.5f : 1.2f) * dt;
        Offset += Math.Clamp(target - Offset, -rate, rate);
        (Pilot.Offset, Pilot.SpeedCap) = (Offset, cap);
        var input = Pilot.Drive(car);

        // drift style: a short handbrake flick at the turn-in of a hairpin, once per corner
        if (Style.Drift > 0.5f && v > 14 && _time >= _flickUntil && s > _flickDoneAt)
        {
            var (k, at) = Pilot.BendAhead(s, 25);
            if (MathF.Abs(k) > 1 / 30f && at < 12)
            {
                _flickUntil = _time + 0.1f + 0.1f * Style.Drift;
                _flickDoneAt = s + at + 40;
            }
        }
        if (_time < _flickUntil) input = input with { Handbrake = true, Throttle = MathF.Max(input.Throttle, 0.4f), Brake = 0 };
        return input;
    }

    /// <summary>Forgets traffic and flick state (car reset onto the line).</summary>
    public void Reset() => (_fresh, _passSide, _flickUntil, _flickDoneAt, Pilot.Offset, Pilot.SpeedCap) = (true, 0, 0, float.MinValue, 0, float.PositiveInfinity);
}
