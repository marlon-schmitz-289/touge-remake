using System.Numerics;

namespace Kansei.Physics;

/// <summary>
///     Drives one planned drift through a corner of a <see cref="RacingLine"/> (pad-style input, the same helpers a player
///     has): <b>entry</b> (handbrake flick, feint, braking drift; FF only a short handbrake tuck), <b>hold</b> (a
///     body slip command from a PI on the path curvature against the line's — wide of the line or pointing out = tighter —
///     and the steering on β, throttle on the planned speed; steering and throttle kept above the drift layer's thresholds),
///     <b>exit</b> (slip command ramped to 0, the pilot takes over; also when the slide fades early) and <b>abort</b> to grip
///     (too much slip, too close to the inside edge, a wall). Entry times from DriftHoldProbe: 0.3 s of handbrake from
///     55 km/h, 0.5 s below.
/// </summary>
public sealed class DriftController
{
    private const float Deg = 180 / MathF.PI;

    /// <summary>
    ///     Handbrake flick; braking drift (trail-braked into the turn-in, then the flick); feint (a flick of the wheel away
    ///     from the bend first); FF: a short tuck, then grip.
    /// </summary>
    public enum Entry { Handbrake, BrakingDrift, Feint, Tuck }
    public enum Phase { Idle, Entry, Hold, Exit, Abort }

    /// <summary>FF: handbrake time of the tuck that points the nose in at a hairpin's turn-in (s): β 6–10°.</summary>
    public const float TuckTime = 0.26f;

    /// <summary>Handbrake flick: from <see cref="FlickSpeed"/> on, below; then until the tail is out, at most this much longer (s).</summary>
    private const float FlickFast = 0.3f, FlickSlow = 0.5f, FlickMore = 0.4f, FlickSpeed = 55 / 3.6f;

    /// <summary>Feint: the wheel <see cref="FeintSteer"/> away from the bend for this long, then a shorter flick (s).</summary>
    public const float FeintTime = 0.15f;
    private const float FeintSteer = 0.5f, FeintFlick = 0.2f;

    /// <summary>
    ///     Hold: the slip command is the style's plus a PI on the path-curvature error (rad per 1/m, and per 1/m·s), trimmed
    ///     at most this much down (to a near grip slide) and up; the pursuit preview is this share of the line pilot's.
    /// </summary>
    private const float TrimP = 8, TrimI = 5, TrimDown = 25 / Deg, TrimUp = 5 / Deg, Preview = 0.6f;

    /// <summary>Slip command limits (rad), and at most this share of the heading still to turn (straightening up to the exit).</summary>
    private const float MinCommand = 6 / Deg, MaxCommand = 40 / Deg, CommandPerRemaining = 0.5f;

    /// <summary>Steering P on the slip (per rad), and the least steering and throttle that keep the car's drift layer engaged (it needs 0.3).</summary>
    private const float SteerP = 3, Engage = 0.32f;

    /// <summary>Throttle: base and P on the speed the wanted curvature allows (per m/s).</summary>
    private const float ThrottleBase = 0.6f, ThrottleP = 0.3f;

    /// <summary>Exit when the travel is within this of the corner's exit heading or this far before its end; over this long, at this throttle.</summary>
    private const float ExitHeading = 15 / Deg, ExitBeforeEnd = 5, ExitTime = 0.4f, ExitThrottle = 0.55f;

    public Phase State { get; private set; }
    /// <summary>Corner index of the current/last drift.</summary>
    public int Corner { get; private set; } = -1;
    /// <summary>Drifts held / aborted / faded early (the slide died out, a 4WD power slide's usual end) so far (statistics).</summary>
    public int Held { get; private set; }
    public int Aborted { get; private set; }
    public int Faded { get; private set; }
    /// <summary>Aborts by reason: too much slip, inside edge, wall.</summary>
    public int[] AbortWhy { get; } = new int[3];
    /// <summary>The current/last drift ended before the corner's exit (abort or fade): grip for the rest of it.</summary>
    public bool EndedEarly { get; private set; }
    /// <summary>Last slip command (rad, toward the bend).</summary>
    public float Command { get; private set; }
    public string Debug { get; private set; } = "";

    private Entry _entry;
    private float _t, _integral, _heading, _kappa, _exitFrom, _hb;
    private int _dir;

    /// <summary>Body slip held without traffic/feedback (rad): 12° + 18° × drift style; 4WD at most 15°.</summary>
    public static float StyleBeta(float drift, CarSpec spec) =>
        MathF.Min((12 + 18 * drift) / Deg, spec.DriveFront > 0 && spec.DriveFront < 1 ? 15 / Deg : 40 / Deg);

    /// <summary>Starts a drift into corner <paramref name="corner"/> (index <paramref name="index"/>) now.</summary>
    public void Start(Vehicle car, CourseMap.Corner corner, int index, Entry entry)
    {
        (State, Corner, _entry, _dir, _t, _integral, EndedEarly) = (Phase.Entry, index, entry, corner.Dir, 0, 0, false);
        var v = car.Velocity.Length();
        _hb = entry switch { Entry.Tuck => TuckTime, Entry.Feint => FeintTime + FeintFlick, _ => v >= FlickSpeed ? FlickFast : FlickSlow };
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

        if (State == Phase.Entry)
        {
            var tailOut = beta >= 10 / Deg;
            switch (_entry)
            {
                case Entry.Tuck when _t < _hb:
                    return new VehicleInput(0.3f, 0, steerIn * 0.8f, Handbrake: true);
                case Entry.Tuck:
                    State = Phase.Idle; // FF: the tuck only points the nose, then grip
                    return null;
                case Entry.Feint when _t < FeintTime:
                    return new VehicleInput(0.6f, 0, -steerIn * FeintSteer); // away from the bend: the weight swings over
                default:
                    if (_t < _hb || !tailOut && _t < _hb + FlickMore)
                        return new VehicleInput(0.5f, 0, steerIn, Handbrake: true); // the handbrake until the tail is out
                    break;
            }
            (State, _t) = (Phase.Hold, 0);
            Held++;
        }

        // exit: the travel has turned through the bend, or its end is near
        var endHeading = Heading(line.PositionAt(c.To + 12) - line.PositionAt(c.To + 4));
        var remaining = MathF.IEEERemainder(endHeading - h, MathF.Tau) * _dir;
        if (State == Phase.Hold && (remaining < ExitHeading || s > c.To - ExitBeforeEnd)) (State, _t, _exitFrom) = (Phase.Exit, 0, Command);

        if (State == Phase.Hold)
        {
            // too much slip, the inside edge close, or a wall: catch it and grip
            var room = line.Map.Room(s);
            var inside = _dir > 0 ? room.Left - lat : room.Right + lat;
            var why = beta > 40 / Deg || v < 7 ? 0 : inside < 1.0f ? 1 : car.WallContacts > 0 ? 2 : -1;
            if (why >= 0)
            {
                (State, _t, EndedEarly) = (Phase.Abort, 0, true);
                Aborted++;
                AbortWhy[why]++;
            }
            else if (_t > 0.4f && beta < 5 / Deg)
            {
                // the slide died out (0.4 s into the hold): ease back to grip like a normal exit
                (State, _t, _exitFrom, EndedEarly) = (Phase.Exit, 0, Command, remaining > 35 / Deg);
                if (EndedEarly) Faded++;
            }
        }
        if (State == Phase.Abort)
        {
            if (_t > 0.6f || MathF.Abs(beta) < 5 / Deg) { State = Phase.Idle; return null; }
            return new VehicleInput(0.3f, 0, Math.Clamp(_dir * SteerP * beta, -1, 1));
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
            var preview = Math.Clamp(4 + 0.4f * v, 6, 25) * Preview;
            var kRef = line.CurvatureAt(s) * _dir + 2 * wide / (preview * preview) + 2 * outPsi / preview;
            var err = kRef - _kappa;
            // the slip: the style's, nudged by the curvature error (tighter wanted = more slip)
            var trim = TrimP * err + _integral;
            if (MathF.Abs(trim) < TrimDown) _integral += TrimI * err * dt; // anti-windup
            Command = Math.Clamp(betaStyle + Math.Clamp(trim, -TrimDown, TrimUp) + noise, MinCommand, MaxCommand);
            // straighten up towards the exit: the slip shrinks with the heading still to turn (no big angle left to scrub off)
            Command = MathF.Min(Command, MathF.Max(remaining * CommandPerRemaining, MinCommand));
            // the path: the speed the wanted curvature allows at the plan's lateral grip (slower = tighter)
            var kLine = MathF.Max(line.CurvatureAt(s) * _dir, 0.002f);
            var vRef = line.SpeedAt(s) * MathF.Sqrt(kLine / MathF.Max(kRef, 0.002f));
            var throttle = Math.Clamp(ThrottleBase + ThrottleP * (vRef - v), Engage, 1);
            if (beta < 0.6f * Command && car.Spec.DriveFront < 1) throttle = MathF.Max(throttle, 0.8f); // power the tail out
            Debug = $"κl {kLine:F3} κr {kRef:F3} κm {_kappa:F3} wide {wide:+0.0;-0.0} ψo {outPsi * Deg:+0;-0} trim {trim * Deg:+0;-0} vr {vRef * 3.6f:F0}";
            return new VehicleInput(throttle, 0, Steer(beta, Command));
        }

        // exit: the pilot steers back onto the line at once (its steering undoes the car's own counter-steer), the throttle
        // eases from the drift's to the planner's so the tail settles instead of snapping
        if (_t >= ExitTime) { State = Phase.Idle; return null; }
        var k = _t / ExitTime;
        Command = _exitFrom * (1 - k);
        // still sliding hard: catch it first (counter-steer on the slip), then the pilot
        if (beta > 20 / Deg) return new VehicleInput(ExitThrottle, 0, Steer(beta, Command));
        return pilot with { Throttle = MathF.Max(pilot.Throttle, ExitThrottle), Brake = pilot.Brake * k };
    }

    /// <summary>P on the slip, at least <see cref="Engage"/> of lock so the drift layer stays engaged.</summary>
    private float Steer(float beta, float cmd)
    {
        var steer = Math.Clamp(_dir * SteerP * (beta - cmd), -1, 1);
        // inside the dead band: counter-steer when the slip is about there (the car adds its own counter-steer on top)
        return MathF.Abs(steer) >= Engage ? steer : beta >= cmd ? Engage * _dir : -Engage * _dir;
    }

    private static float Heading(Vector3 v) => MathF.Atan2(v.X, v.Z);
}
