using System.Numerics;
using Kansei.Graphics;

namespace Touge;

/// <summary>
///     The car's light switch: <see cref="Mode"/> off / low / high beam (L lights on/off, H high beam on/off — also switches
///     the lights on; pad D-pad up/down), on by default at night and in rain, off by day. Pop-up lamps travel
///     <see cref="PopUpSeconds"/> and light up only once nearly open. <see cref="Apply"/> puts the car's lamps into the
///     <see cref="SceneLights"/>: two beams at the lens centres along the car's axis, lens glow, rear lamps (running, brake,
///     reverse) as small red point lights.
/// </summary>
public sealed class Headlights(Headlights.Mode start)
{
    public enum Mode { Off, Low, High }

    public const float PopUpSeconds = 0.6f;

    /// <summary>
    ///     Beam peak per lamp, warm halogen: × 1/d² on the beam axis, a flat road gets ~0.15–0.4 from 5 to 60 m (low beam;
    ///     the night moon is 0.12). Close surfaces saturate at lighting.glsl's cap.
    /// </summary>
    public static readonly Vector3 Beam = new Vector3(1f, 0.93f, 0.82f) * 18000;

    public Mode State { get; private set; } = start;

    /// <summary>Pop-up lamps: 0 closed … 1 open.</summary>
    public float Open { get; private set; } = start == Mode.Off ? 0 : 1;

    /// <summary>Lights at the start of a course: on at night and in rain, off by day.</summary>
    public static Mode For(string courseTime) => courseTime.EndsWith("_DAY") ? Mode.Off : Mode.Low;

    public void Toggle() => State = State == Mode.Off ? Mode.Low : Mode.Off;

    public void ToggleHigh() => State = State == Mode.High ? Mode.Low : Mode.High;

    public void Tick(float dt) => Open = Math.Clamp(Open + (State == Mode.Off ? -dt : dt) / PopUpSeconds, 0, 1);

    /// <summary>How far the headlamps are lit, 0..1: switched on and, for pop-ups, the last fifth of the way up.</summary>
    public float Lit(bool popUp) => State == Mode.Off ? 0 : popUp ? Math.Clamp((Open - 0.8f) / 0.2f, 0, 1) : 1;

    /// <summary>
    ///     Lamps of the car at <paramref name="body"/> (car → world) into <paramref name="l"/>; <paramref name="daylight"/> =
    ///     <see cref="Atmosphere.LocalLightShare"/> (dims the lens glow by day), brake 0..1, reversing.
    /// </summary>
    public void Apply(SceneLights l, CarModel.Lamps lamps, in Matrix4x4 body, float daylight, float brake, bool reverse)
    {
        var lit = Lit(lamps.PopUp);
        var dir = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ, body));
        for (var i = 0; i < 2; i++)
        {
            l.HeadlightPosition[i] = Vector3.Transform(lamps.Head[i], body);
            l.HeadlightDirection[i] = dir;
            l.TailLightPosition[i] = Vector3.Transform(lamps.Tail[i], body);
        }
        l.HeadlightColor = Beam * lit;
        l.HighBeam = State == Mode.High ? 1 : 0;
        l.LampGlow = lit * (State == Mode.High ? 1.3f : 1) * (0.35f + 0.65f * daylight);
        (l.Brake, l.Reverse) = (brake, reverse ? 1 : 0);
        l.TailLightColor = new Vector3(1f, 0.08f, 0.03f) * ((State == Mode.Off ? 0 : 0.08f) + 0.8f * brake);
    }
}
