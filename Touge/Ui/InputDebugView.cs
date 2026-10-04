using System.Numerics;
using Kansei.Graphics;
using Kansei.Input;

namespace Touge.Ui;

/// <summary>
///     --input-debug: a panel at the right edge with every device (keyboard, pad, each joystick with axes as bars, held
///     buttons, hats, force-feedback support), what the game makes of it (<see cref="DriverInput"/>: steering, pedals,
///     handbrake, H-shifter gear, active device) and the force feedback split into its parts (<see cref="ForceFeedback"/>).
///     --sim-wheel drives a virtual wheel through <see cref="Simulate"/>.
/// </summary>
public static class InputDebugView
{
    /// <summary>
    ///     Virtual wheel motion: steering swings ±40 % of the axis every 8 s, throttle (axis 2, resting at +1 like a Logitech
    ///     pedal) pumps, brake (axis 3) taps every 5 s, the hat points up for a moment every 4 s.
    /// </summary>
    public static void Simulate(JoystickState w, double t)
    {
        var s = (float)t + 2; // starts at a full swing (screenshots)
        w.SetAxis(0, 0.4f * MathF.Sin(s * MathF.Tau / 8));
        w.SetAxis(1, 1 - 2 * Math.Clamp(0.6f + 0.5f * MathF.Sin(s * 1.3f), 0, 1));
        w.SetAxis(2, s % 5 < 0.8f ? -0.6f : 1);
        w.SetAxis(3, 1);
        w.SetHat(0, (byte)(s % 4 < 0.3f ? 1 : 0));
    }

    public static void Build(Overlay o, int width, int height, InputSnapshot input, DriverInput d, ForceFeedback ffb)
    {
        var u = height / 1080f;
        float x0 = width - 470 * u, x1 = width - 20 * u, y = 90 * u, line = 22 * u;
        var lines = 9 + input.Joysticks.Sum(j => 2 + Math.Min(j.AxisCount, 8));
        o.Rect(new Vector2(x0 - 12 * u, y - 26 * u), new Vector2(x1 + 8 * u, y + lines * line), Overlay.Rgba(0, 0, 0, 0.7f));
        void Text(string s, uint color = 0) => (y, _) = (y + line, Style.Label(o, s, new Vector2(x0, y), 17 * u, color == 0 ? Style.Text : color));
        void Bar(string label, float v, bool centred)
        {
            Style.Label(o, label, new Vector2(x0, y), 15 * u, Style.Dim);
            float b0 = x0 + 130 * u, b1 = x1 - 70 * u, mid = centred ? (b0 + b1) / 2 : b0, at = mid + v * (centred ? (b1 - b0) / 2 : b1 - b0);
            o.Rect(new Vector2(b0, y - 12 * u), new Vector2(b1, y - 2 * u), Overlay.Rgba(1, 1, 1, 0.12f));
            o.Rect(new Vector2(MathF.Min(mid, at), y - 12 * u), new Vector2(MathF.Max(mid, at), y - 2 * u), Style.Amber);
            Style.Label(o, FormattableString.Invariant($"{v:+0.000;-0.000;0.000}"), new Vector2(x1, y), 15 * u, Style.Text, 1);
            y += line * 0.8f;
        }

        Text("INPUT DEBUG", Style.Amber);
        Text($"KEYBOARD   PAD {(input.Gamepad.IsConnected ? input.ActiveName : "none")}   ACTIVE {d.Active.ToString().ToUpperInvariant()}");
        Bar("STEER", d.Steer, true);
        Bar("THROTTLE", d.Throttle, false);
        Bar("BRAKE", d.Brake, false);
        Bar("CLUTCH", d.Clutch, false);
        Text($"HANDBRAKE {(d.Handbrake ? "ON" : "off")}   {(d.DirectSteer ? "DIRECT STEER" : "ASSISTED STEER")}   H-GEAR {(d.HGear?.ToString() ?? "-")}");
        Bar("FFB OUT", ffb.Output, true);
        Text(FormattableString.Invariant($"aligning {ffb.Aligning:+0.00;-0.00}  kerb {ffb.Kerb:+0.00;-0.00}  jolt {ffb.Jolt:+0.00;-0.00}  lock {d.SteerBeyond:+0.00;-0.00}"), Style.Dim);
        foreach (var j in input.Joysticks)
        {
            Text($"{(j == d.Wheel ? "> " : "")}{j.Name}{(j.IsWheel ? " [wheel]" : "")}{(j.IsGameController ? " [pad]" : "")}{(j.HasForceFeedback ? " [FFB]" : "")}", j == d.Wheel ? Style.Green : 0);
            for (var i = 0; i < Math.Min(j.AxisCount, 8); i++) Bar($"axis {i + 1}", j.Axis(i), true);
            var held = string.Join(" ", Enumerable.Range(0, j.ButtonCount).Where(j.Button).Select(b => (b + 1).ToString()));
            Text(FormattableString.Invariant($"buttons {(held == "" ? "-" : held)}   hats {string.Join(" ", Enumerable.Range(0, j.HatCount).Select(j.Hat))}   force {j.Force:+0.00;-0.00;0.00}"), Style.Dim);
        }
        if (input.Joysticks.Count == 0) Text("no joystick / wheel connected", Style.Dim);
    }
}
