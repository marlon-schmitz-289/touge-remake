using System.Numerics;
using Kansei.Input;

namespace Touge.Replays;

/// <summary>
///     Free camera of the replay viewer and photo mode, held inside the course (<see cref="Hull"/>). Keyboard: W/A/S/D move, Q/E down/up, I/J/K/L look, Shift fast,
///     right mouse drag look. Pad: left stick move, right stick look, triggers down/up, left stick click fast.
/// </summary>
public sealed class FreeCam
{
    public Vector3 Position;
    public float Yaw, Pitch;
    /// <summary>The course it may not fly through or out of (<see cref="CameraHull.Reach"/>): it stops before surfaces and the edge.</summary>
    public CameraHull? Hull { get; set; }
    private int _mouseX, _mouseY;

    public Vector3 Forward => new(MathF.Sin(Yaw) * MathF.Cos(Pitch), MathF.Sin(Pitch), MathF.Cos(Yaw) * MathF.Cos(Pitch));

    /// <summary>Starts at <paramref name="eye"/> looking at <paramref name="look"/>.</summary>
    public void Place(Vector3 eye, Vector3 look)
    {
        var d = Vector3.Normalize(look - eye + new Vector3(0, 0, 1e-6f));
        (Position, Yaw, Pitch) = (eye, MathF.Atan2(d.X, d.Z), MathF.Asin(Math.Clamp(d.Y, -1, 1)));
    }

    public void Update(InputSnapshot input, float dt)
    {
        var k = input.Keyboard;
        var pad = input.Gamepad;
        var m = input.Mouse;
        if (m.IsButtonDown(3))
        {
            Yaw -= (m.X - _mouseX) * 0.004f;
            Pitch -= (m.Y - _mouseY) * 0.004f;
        }
        (_mouseX, _mouseY) = (m.X, m.Y);
        float lookX = (k.IsKeyDown(Key.J) ? 1 : 0) - (k.IsKeyDown(Key.L) ? 1 : 0), lookY = (k.IsKeyDown(Key.I) ? 1 : 0) - (k.IsKeyDown(Key.K) ? 1 : 0);
        Vector2 move = new((k.IsKeyDown(Key.D) ? 1 : 0) - (k.IsKeyDown(Key.A) ? 1 : 0), (k.IsKeyDown(Key.W) ? 1 : 0) - (k.IsKeyDown(Key.S) ? 1 : 0));
        float up = (k.IsKeyDown(Key.E) ? 1 : 0) - (k.IsKeyDown(Key.Q) ? 1 : 0);
        var fast = k.IsKeyDown(Key.LeftShift);
        if (pad.IsConnected)
        {
            static float Dead(float v) => MathF.Abs(v) < 0.15f ? 0 : (v - MathF.CopySign(0.15f, v)) / 0.85f;
            lookX -= Dead(pad.GetAxis(GamepadAxis.RightX));
            lookY -= Dead(pad.GetAxis(GamepadAxis.RightY));
            move += new Vector2(Dead(pad.GetAxis(GamepadAxis.LeftX)), -Dead(pad.GetAxis(GamepadAxis.LeftY)));
            up += pad.GetAxis(GamepadAxis.TriggerRight) - pad.GetAxis(GamepadAxis.TriggerLeft);
            fast |= pad.IsButtonDown(GamepadButton.LeftStick);
        }
        Yaw += lookX * 1.8f * dt;
        Pitch = Math.Clamp(Pitch + lookY * 1.2f * dt, -1.5f, 1.5f);
        var fwd = Forward;
        var right = Vector3.Normalize(Vector3.Cross(fwd, Vector3.UnitY));
        var v = fwd * move.Y + right * move.X + Vector3.UnitY * up;
        if (v.LengthSquared() > 1) v = Vector3.Normalize(v);
        var step = v * (fast ? 60f : 12f) * dt;
        var len = step.Length();
        if (len > 0) Position += step * (MathF.Min(len, Hull?.Reach(Position, Position + step) ?? len) / len);
    }
}
