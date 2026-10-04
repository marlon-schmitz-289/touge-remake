using System.Numerics;

namespace Touge.Net;

public enum SampleKind { None, Interpolated, Extrapolated, Held }

/// <summary>
///     The last <see cref="Capacity"/> states of one remote car, ordered by the sender's race time (packets may arrive late or
///     twice: duplicates and ones older than the window are dropped). <see cref="Sample"/> at race time t: between two states
///     a cubic Hermite curve through both positions with both velocities as tangents (no corner cutting on a drift) and a slerp
///     of the orientation; past the newest one dead reckoning along its velocity and spin for at most
///     <see cref="MaxExtrapolation"/> s, then the car is held there (a peer that went quiet does not drive on through walls).
///     Counts received, lost (sequence gaps) and late packets.
/// </summary>
public sealed class SnapshotBuffer
{
    public const int Capacity = 32;
    public const float MaxExtrapolation = 0.5f;

    private readonly CarState[] _s = new CarState[Capacity];
    private int _n;
    private uint _maxSeq;

    public int Received { get; private set; }
    public int Late { get; private set; }
    public int Duplicates { get; private set; }
    /// <summary>Sequence numbers skipped so far that never came (late ones are taken back out).</summary>
    public long Lost { get; private set; }
    public CarState? Latest => _n > 0 ? _s[_n - 1] : null;
    public int Count => _n;

    public void Clear() => (_n, _maxSeq, Received, Late, Duplicates, Lost) = (0, 0, 0, 0, 0, 0);

    public void Add(in CarState s)
    {
        for (var i = 0; i < _n; i++)
            if (_s[i].Seq == s.Seq)
            {
                Duplicates++;
                return;
            }
        Received++;
        if (Received > 1 && s.Seq > _maxSeq + 1) Lost += s.Seq - _maxSeq - 1;
        else if (Received > 1 && s.Seq < _maxSeq)
        {
            Late++;
            Lost = Math.Max(0, Lost - 1);
        }
        _maxSeq = Received == 1 ? s.Seq : Math.Max(_maxSeq, s.Seq);
        // insert by time; full: the oldest goes (or this one, if it is older than all)
        var at = _n;
        while (at > 0 && _s[at - 1].Time > s.Time) at--;
        if (_n == Capacity)
        {
            if (at == 0) return;
            Array.Copy(_s, 1, _s, 0, at - 1);
            _s[at - 1] = s;
            return;
        }
        Array.Copy(_s, at, _s, at + 1, _n - at);
        _s[at] = s;
        _n++;
    }

    /// <summary>The car at race time <paramref name="t"/> into <paramref name="r"/>; <see cref="SampleKind.None"/> while empty.</summary>
    public SampleKind Sample(float t, out CarState r)
    {
        r = default;
        if (_n == 0) return SampleKind.None;
        if (t <= _s[0].Time)
        {
            r = _s[0];
            return SampleKind.Held;
        }
        var last = _s[_n - 1];
        if (t >= last.Time)
        {
            var dt = t - last.Time;
            r = Extrapolate(last, MathF.Min(dt, MaxExtrapolation));
            if (dt <= MaxExtrapolation) return SampleKind.Extrapolated;
            r = r with { Velocity = Vector3.Zero, AngularVelocity = Vector3.Zero }; // held: shown standing, not drifting on
            return SampleKind.Held;
        }
        var i = 1;
        while (_s[i].Time < t) i++;
        r = Interpolate(_s[i - 1], _s[i], t);
        return SampleKind.Interpolated;
    }

    public static CarState Extrapolate(in CarState s, float dt)
    {
        var w = s.AngularVelocity;
        var angle = w.Length() * dt;
        var q = angle > 1e-6f ? Quaternion.Normalize(Quaternion.Concatenate(s.Orientation, Quaternion.CreateFromAxisAngle(Vector3.Normalize(w), angle))) : s.Orientation;
        return s with { Time = s.Time + dt, Position = s.Position + s.Velocity * dt, Orientation = q, Along = s.Along + Vector3.Dot(s.Velocity, Vector3.Transform(Vector3.UnitZ, s.Orientation)) * dt };
    }

    public static CarState Interpolate(in CarState a, in CarState b, float t)
    {
        var span = b.Time - a.Time;
        var u = span > 1e-5f ? (t - a.Time) / span : 1;
        float u2 = u * u, u3 = u2 * u;
        // cubic Hermite basis: position follows both ends' velocities
        float h00 = 2 * u3 - 3 * u2 + 1, h10 = u3 - 2 * u2 + u, h01 = -2 * u3 + 3 * u2, h11 = u3 - u2;
        var p = h00 * a.Position + h10 * span * a.Velocity + h01 * b.Position + h11 * span * b.Velocity;
        var near = u < 0.5f ? a : b;
        return near with
        {
            Time = t, Position = p, Orientation = Quaternion.Slerp(a.Orientation, b.Orientation, u),
            Velocity = Vector3.Lerp(a.Velocity, b.Velocity, u), AngularVelocity = Vector3.Lerp(a.AngularVelocity, b.AngularVelocity, u),
            Steer = float.Lerp(a.Steer, b.Steer, u), Throttle = float.Lerp(a.Throttle, b.Throttle, u), Brake = float.Lerp(a.Brake, b.Brake, u),
            Rpm = float.Lerp(a.Rpm, b.Rpm, u), Along = float.Lerp(a.Along, b.Along, u),
        };
    }
}
