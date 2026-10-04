using System.Numerics;
using Kansei.Graphics;
using Kansei.Input;

namespace Touge.Ui;

/// <summary>
///     Replay viewer controls and overlay (the game plays the cars, <see cref="Replays.ReplayPlayer"/>). The original shows a
///     blinking REPLAY and cuts between trackside TV cameras; on top of that: pause, speed ¼×–4×, rewind/fast forward,
///     chase/bumper/free camera, the other car in a battle, overlay off, photo mode.
///     Keyboard: Space/Enter pause, ←/→ hold rewind/forward, ↑/↓ speed, C camera, Tab car, H overlay, P photo, R from the start,
///     Esc/Backspace exit. Pad: A/Start pause, D-pad ←/→ or LB/RB rewind/forward, D-pad ↑/↓ speed, Y camera, Back car, X overlay,
///     right stick click photo, left stick click from the start, B exit. Free camera: <see cref="Replays.FreeCam"/>.
/// </summary>
public sealed class ReplayViewer
{
    public enum Camera { Tv, Chase, Bumper, Free }
    public enum Command { None, Exit, Photo, Restart }

    public static readonly float[] Speeds = [0.25f, 0.5f, 1, 2, 4];
    private static readonly string[] CameraNames = ["TV CAMERA", "CHASE", "BUMPER", "FREE CAMERA"];

    public bool Active { get; private set; }
    public bool Paused { get; set; }
    public int SpeedIndex { get; private set; } = 2;
    public Camera Cam { get; private set; } = Camera.Tv;
    public bool OverlayHidden { get; private set; }
    /// <summary>Car the cameras follow (0 the player's).</summary>
    public int Focus { get; private set; }
    /// <summary>−1 rewinding, +1 fast forward (held), 0 playing at <see cref="Speed"/>.</summary>
    public int Scrub { get; private set; }
    /// <summary>Ticks per fixed tick: 0 paused or scrubbing.</summary>
    public float Speed => Paused || Scrub != 0 ? 0 : Speeds[SpeedIndex];

    private float _clock, _flash;
    private string _flashText = "";

    public void Open(Camera cam = Camera.Tv)
    {
        (Active, Paused, SpeedIndex, Cam, OverlayHidden, Focus, Scrub, _flash) = (true, false, 2, cam, false, 0, 0, 0);
    }

    public void Close() => Active = false;

    private void Flash(string text) => (_flashText, _flash) = (text, 1.6f);

    /// <summary>One frame: <paramref name="keys"/> (<see cref="MenuKeys"/>: DECIDE pause, BACK exit), the rest straight from <paramref name="input"/>.</summary>
    public Command Update((int X, int Y, bool Ok, bool Back) keys, InputSnapshot input, float dt, int cars, Action<string>? sound)
    {
        _clock += dt;
        _flash = MathF.Max(0, _flash - dt);
        var k = input.Keyboard;
        var pad = input.Gamepad;
        bool P(Key key, GamepadButton b) => k.IsKeyPressed(key) || pad.IsConnected && pad.IsButtonPressed(b);
        if (keys.Back)
        {
            sound?.Invoke("BEEP001");
            return Command.Exit;
        }
        if (keys.Ok)
        {
            Paused = !Paused;
            sound?.Invoke("SYS005");
        }
        if (P(Key.Up, GamepadButton.DpadUp) && SpeedIndex < Speeds.Length - 1) ChangeSpeed(+1, sound);
        if (P(Key.Down, GamepadButton.DpadDown) && SpeedIndex > 0) ChangeSpeed(-1, sound);
        if (P(Key.C, GamepadButton.Y))
        {
            Cam = (Camera)(((int)Cam + 1) % CameraNames.Length);
            sound?.Invoke("SYS005");
        }
        if (P(Key.Tab, GamepadButton.Back) && cars > 1)
        {
            Focus = (Focus + 1) % cars;
            sound?.Invoke("SYS005");
        }
        if (P(Key.H, GamepadButton.X)) OverlayHidden = !OverlayHidden;
        if (P(Key.R, GamepadButton.LeftStick))
        {
            sound?.Invoke("SYS006");
            return Command.Restart;
        }
        if (P(Key.P, GamepadButton.RightStick))
        {
            sound?.Invoke("SYS006");
            return Command.Photo;
        }
        // the free camera flies with WASD: scrubbing only on the arrows there
        bool Held(Key arrow, Key letter, GamepadButton b1, GamepadButton b2) =>
            k.IsKeyDown(arrow) || Cam != Camera.Free && k.IsKeyDown(letter) || pad.IsConnected && (pad.IsButtonDown(b1) || pad.IsButtonDown(b2));
        Scrub = (Held(Key.Right, Key.D, GamepadButton.DpadRight, GamepadButton.RightShoulder) ? 1 : 0) - (Held(Key.Left, Key.A, GamepadButton.DpadLeft, GamepadButton.LeftShoulder) ? 1 : 0);
        return Command.None;
    }

    private void ChangeSpeed(int step, Action<string>? sound)
    {
        SpeedIndex += step;
        Flash(SpeedText);
        sound?.Invoke("SYS005");
    }

    private string SpeedText => Speeds[SpeedIndex] switch { 0.25f => "x 1/4", 0.5f => "x 1/2", var s => $"x {s:0}" };

    /// <summary>The overlay (nothing when hidden): blinking REPLAY, the focused car's name and speed, the time line, state, camera, hints.</summary>
    public void Build(Overlay o, int width, int height, Canvas c, float t, float total, string car, float kmh, string gear, bool mph)
    {
        o.Clear();
        if (!Active || OverlayHidden) return;
        c.Begin(o, width, height);
        // REPLAY in the racing orange, blinking as the original's (on 0.8 s, off 0.3 s)
        if (_clock % 1.1f < 0.8f || Paused)
            c.Lettering("REPLAY", c.Left + 26, 52, 34, Overlay.Rgba(1, 0.82f, 0.25f), Overlay.Rgba(1, 0.38f, 0), 0, 0.18f, false);
        var speed = mph ? kmh / 1.609344f : kmh;
        c.Text(car, c.Left + 28, 76, 13, Canvas.White, 0, 0.15f, 0.08f, 0.2f);
        c.Text($"{speed:0} {(mph ? "mph" : "km/h")}   {gear}", c.Left + 28, 94, 13, Canvas.White, 0, 0.15f, 0.08f, 0.2f);
        if (_flash > 0) c.Lettering(_flashText, 256, 240, 40, Style.Fade(Overlay.Rgba(1, 0.82f, 0.25f), MathF.Min(1, _flash * 2)), Style.Fade(Overlay.Rgba(1, 0.38f, 0), MathF.Min(1, _flash * 2)), 0.5f, 0.18f, false);

        // time line: carbon strip, bar with the played part in red, the state symbol and the times
        float x0 = 40, x1 = 472, y = 398;
        c.Carbon(x0 - 16, y - 22, x1 + 16, y + 20, 1, false);
        Vector2 a = c.P(x0 + 70, y - 3), b = c.P(x1 - 110, y + 3);
        o.Rect(Vector2.Round(a), Vector2.Round(b), Overlay.Rgba(0.1f, 0.1f, 0.11f));
        var f = total > 0 ? Math.Clamp(t / total, 0, 1) : 0;
        o.Rect(Vector2.Round(a), Vector2.Round(new Vector2(float.Lerp(a.X, b.X, f), b.Y)), Overlay.Rgba(0.85f, 0.1f, 0.06f));
        c.Diamond(float.Lerp(x0 + 70, x1 - 110, f), y, 5);
        State(c, x0 + 8, y);
        c.Text(SpeedText, x0 + 30, y + 5, 12, Canvas.White, 0, 0.15f, 0, 0.2f);
        c.Text($"{Style.Time(t)} / {Style.Time(total)}", x1 - 2, y + 5, 12, Canvas.White, 1, 0.15f, 0, 0.2f);
        c.Text(CameraNames[(int)Cam], x1 + 10, y - 26, 13, Canvas.White, 1, 0.15f, 0.08f, 0.3f); // top right belongs to NOW PLAYING
        Menu.Hint(c, Cam == Camera.Free
            ? "WASD/QE: Fly  IJKL/MOUSE: Look  SPACE: Pause  ARROWS: Rewind/Speed  C: Camera  H: Hide  P: Photo  ESC: Exit"
            : "SPACE: Pause  LEFT/RIGHT: Rewind/Forward  UP/DOWN: Speed  C: Camera  TAB: Car  H: Hide  P: Photo  ESC: Exit");
    }

    /// <summary>▶ playing, ❚❚ paused, ◀◀ / ▶▶ scrubbing, in yellow.</summary>
    private void State(Canvas c, float x, float y)
    {
        if (Scrub != 0)
        {
            for (var i = 0; i < 2; i++)
            {
                var dx = Scrub * (i * 9 - 4);
                c.Arrow(x + dx - 4 * Scrub, y - 6, x + dx - 4 * Scrub, y + 6, x + dx + 5 * Scrub, y);
            }
            return;
        }
        if (Paused)
        {
            c.O.Rect(Vector2.Round(c.P(x - 6, y - 6)), Vector2.Round(c.P(x - 2, y + 6)), Canvas.Yellow);
            c.O.Rect(Vector2.Round(c.P(x + 2, y - 6)), Vector2.Round(c.P(x + 6, y + 6)), Canvas.Yellow);
            return;
        }
        c.Arrow(x - 5, y - 7, x - 5, y + 7, x + 7, y);
    }
}
