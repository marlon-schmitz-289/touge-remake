using System.Numerics;

namespace Kansei.Physics;

/// <summary>
///     Drives one planned drift through a corner of a <see cref="RacingLine"/> (pad-style input, the same helpers a player
///     has): <b>entry</b> (handbrake, feint, power-over, braking drift; FF only a short handbrake tuck), <b>hold</b> (a body
///     slip command from a PI on the path curvature against the line's — wide of the line or pointing out = tighter — and
///     the steering on β, throttle on the planned speed; steering and throttle kept above the drift layer's thresholds),
///     <b>exit</b> (slip command ramped to 0, the pilot takes over) and <b>abort</b> to grip (too much slip, too close to the
///     inside edge, a wall). Numbers from DriftHoldProbe: β settles at ~0.75 × the P controller's target, hence ×1.33.
/// </summary>
public sealed class DriftController
{
    private const float Deg = 180 / MathF.PI;

    /// <summary>Handbrake flick; braking drift (trail-braked into the turn-in, then the flick); FF: a short tuck, then grip.</summary>
    public enum Entry { Handbrake, BrakingDrift, Tuck }
    public enum Phase { Idle, Entry, Hold, Exit, Abort }

    public Phase State { get; private set; }
    /// <summary>Corner index of the current/last drift.</summary>
    public int Corner { get; private set; } = -1;
    /// <summary>Drifts held / aborted so far (statistics).</summary>
    public int Held { get; private set; }
    public int Aborted { get; private set; }
    /// <summary>Aborts by reason: too much slip, inside edge, wall, no slip.</summary>
    public int[] AbortWhy { get; } = new int[4];
    /// <summary>Last slip command (rad, toward the bend).</summary>
    public float Command { get; private set; }
    public string Debug { get; private set; } = "";
    public static float[] Tune = Environment.GetEnvironmentVariable("DRIFT_P") is { } dp ? [.. dp.Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture))] : [8, 5, 0.6f, 0.6f, 0.35f, 0.3f, 4f, 15f, 1f, 5f, 0.55f, 0f, 15f, 5f, 0.5f, 0.12f, 0.25f];

    private Entry _entry;
    private float _t, _integral, _heading, _kappa, _exitFrom, _hb;
    private int _dir;

    /// <summary>Body slip held without traffic/feedback (rad): 12° + 18° × drift style; 4WD at most 15°.</summary>
    public static float StyleBeta(float drift, CarSpec spec) =>
        MathF.Min((12 + 18 * drift) / Deg, spec.DriveFront > 0 && spec.DriveFront < 1 ? 15 / Deg : 40 / Deg);

    /// <summary>Starts a drift into corner <paramref name="corner"/> (index <paramref name="index"/>) now.</summary>
    public void Start(Vehicle car, CourseMap.Corner corner, int index, Entry entry)
    {
        (State, Corner, _entry, _dir, _t, _integral) = (Phase.Entry, index, entry, corner.Dir, 0, 0);
        var v = car.Velocity.Length();
        _hb = entry == Entry.Tuck ? 0.15f : v >= 55 / 3.6f ? 0.3f : 0.5f;
        (_heading, _kappa) = (Heading(car.Velocity), 0);
    }

    /// <summary>Hands back to the pilot at once (traffic, reset).</summary>
    public void Stop() => State = Phase.Idle;

    /// <param name="betaStyle">Slip held (rad, <see cref="StyleBeta"/>).</param>
    /// <param name="noise">Slip command noise this tick (rad): wobble, over-rotation.</param>
    /// <param name="pilot">The pilot's own input this tick (exit throttle).</param>
    /// <returns>Input while in charge, null once the pilot drives again.</returns>
    public VehicleInput? Step(Vehicle car, RacingLine line, CourseMap.Corner c, float s, float lat, float betaStyle, float noise, VehicleInput pilot, float dt)
    {
        if (State == Phase.Idle) return null;
        _t += dt;
        var v = car.Velocity.Length();
        var beta = car.SlipAngle * _dir; // + = tail out of the bend
        var h = Heading(car.Velocity);
        var raw = MathF.IEEERemainder(h - _heading, MathF.Tau) / MathF.Max(v * dt, 0.01f) * _dir; // a left turn raises atan2(X, Z)
        _heading = h;
        _kappa += (raw - _kappa) * 0.3f;
        var steerIn = -_dir; // full lock towards the bend (steer −1 = left)

        switch (State)
        {
            case Phase.Entry:
                if (_t < _hb || _entry != Entry.Tuck && beta < 10 / Deg && _t < _hb + 0.4f)
                    // the handbrake until the tail is out (at most 0.4 s longer)
                    return new VehicleInput(_entry == Entry.Tuck ? 0.3f : 0.5f, 0, steerIn * (_entry == Entry.Tuck ? 0.8f : 1), Handbrake: true);
                if (_entry == Entry.Tuck)
                {
                    State = Phase.Idle; // FF: the tuck only points the nose, then grip
                    return null;
                }
                (State, _t) = (Phase.Hold, 0);
                Held++;
                break;
        }

        // exit: the travel has turned through the bend, or its end is near
        var endHeading = Heading(line.PositionAt(c.To + 12) - line.PositionAt(c.To + 4));
        var remaining = MathF.IEEERemainder(endHeading - h, MathF.Tau) * _dir;
        if (State == Phase.Hold && (remaining < Tune[12] / Deg || s > c.To - Tune[13])) (State, _t, _exitFrom) = (Phase.Exit, 0, Command);

        if (State == Phase.Hold)
        {
            // too much slip, the inside edge close, or a wall: catch it and grip
            var room = line.Map.Room(s);
            var inside = (_dir > 0 ? room.Left - lat : room.Right + lat);
            // …or the tail never came out (no slip 0.4 s into the hold): grip
            var why = beta > 40 / Deg || v < 7 ? 0 : inside < 1.0f ? 1 : car.WallContacts > 0 ? 2 : _t > 0.4f && beta < 5 / Deg && remaining > 35 / Deg ? 3 : -1;
            if (why < 0 && _t > 0.4f && beta < 5 / Deg) (State, _t, _exitFrom) = (Phase.Exit, 0, Command); // straightened near the end: done
            if (why >= 0)
            {
                (State, _t) = (Phase.Abort, 0);
                Aborted++;
                AbortWhy[why]++;
            }
        }
        if (State == Phase.Abort)
        {
            if (_t > 0.6f || MathF.Abs(beta) < 5 / Deg) { State = Phase.Idle; return null; }
            return new VehicleInput(0.3f, 0, Math.Clamp(_dir * 3 * beta, -1, 1));
        }

        if (State == Phase.Hold)
        {
            // heading error against the line's tangent (+ = pointing out of the bend) and metres wide of it
            var tangent = line.PositionAt(s + 3) - line.PositionAt(s - 3);
            var vel = car.Velocity;
            var cross = tangent.X * vel.Z - tangent.Z * vel.X;
            var outPsi = MathF.Atan2(cross, tangent.X * vel.X + tangent.Z * vel.Z) * _dir;
            var wide = -(lat - line.OffsetAt(s)) * _dir;
            // the path curvature wanted: the line's, plus the pilot's pursuit feedback back onto it
            var preview = Math.Clamp(4 + 0.4f * v, 6, 25) * Tune[2];
            var kRef = line.CurvatureAt(s) * _dir + 2 * wide / (preview * preview) + 2 * outPsi / preview;
            var err = kRef - _kappa;
            // the slip: the style's, nudged by the curvature error (tighter wanted = more slip), at most ±8°
            var trim = Tune[0] * err + _integral;
            if (MathF.Abs(trim) < Tune[7] / Deg) _integral += Tune[1] * err * dt; // anti-windup
            Command = Math.Clamp(betaStyle + Math.Clamp(trim, -Tune[7] / Deg, Tune[9] / Deg) + noise, 6 / Deg, 40 / Deg);
            // straighten up towards the exit: the slip shrinks with the heading still to turn (no big angle left to scrub off)
            Command = MathF.Min(Command, MathF.Max(remaining * Tune[14], 6 / Deg));
            // the path: the speed the wanted curvature allows at the plan's lateral grip (slower = tighter)
            var kLine = MathF.Max(line.CurvatureAt(s) * _dir, 0.002f);
            var vRef = line.SpeedAt(s) * MathF.Sqrt(kLine / MathF.Max(kRef, 0.002f));
            var throttle = Math.Clamp(0.6f + Tune[5] * (vRef - v), 0.32f, 1);
            if (beta < 0.6f * Command && car.Spec.DriveFront < 1) throttle = MathF.Max(throttle, 0.8f); // power the tail out
            Debug = $"κl {kLine:F3} κr {kRef:F3} κm {_kappa:F3} wide {wide:+0.0;-0.0} ψo {outPsi * Deg:+0;-0} trim {trim * Deg:+0;-0} vr {vRef * 3.6f:F0}";
            return new VehicleInput(throttle, 0, Steer(beta, Command));
        }

        // exit: the pilot steers back onto the line at once (its steering undoes the car's own counter-steer), the throttle
        // eases from the drift's to the planner's over 0.4 s so the tail settles instead of snapping
        if (_t >= 0.4f) { State = Phase.Idle; return null; }
        Command = _exitFrom * (1 - _t / 0.4f);
        var k = _t / 0.4f;
        // still sliding hard: catch it first (counter-steer on the slip), then the pilot
        if (beta > 20 / Deg) return new VehicleInput(Tune[10], 0, Steer(beta, Command));
        return pilot with { Throttle = MathF.Max(pilot.Throttle, Tune[10]), Brake = pilot.Brake * k };
    }

    /// <summary>P on the slip (×1.33 for the droop), at least 0.32 of lock so the drift layer stays engaged.</summary>
    private float Steer(float beta, float cmd)
    {
        var steer = Math.Clamp(_dir * 3 * (beta - Tune[8] * cmd), -1, 1);
        // inside the dead band: counter-steer when the slip is about there (the car adds its own counter-steer on top)
        return MathF.Abs(steer) >= 0.32f ? steer : beta >= cmd ? 0.32f * _dir : -0.32f * _dir;
    }

    private static float Heading(Vector3 v) => MathF.Atan2(v.X, v.Z);
}
