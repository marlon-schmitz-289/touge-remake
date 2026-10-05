using Kansei.Graphics;
using Kansei.Input;
using Touge.Replays;

namespace Touge.Ui;

/// <summary>
///     PHOTO MODE (pause menu or replay viewer): the game stands still, a free camera (<see cref="FreeCam"/>), field of view
///     and exposure, overlay off, and a PNG of the picture (without any overlay) into the screenshots folder.
///     Keyboard: ↑/↓ field of view, ←/→ exposure, H overlay, Enter/Space take the photo, Esc back. Pad: D-pad ↑/↓ and ←/→, X overlay,
///     A photo, B back.
/// </summary>
public sealed class PhotoMode
{
    public enum Command { None, Exit, Capture }

    public const int FovMin = 10, FovMax = 100;
    public const float EvMin = -2, EvMax = 2;

    public bool Active { get; private set; }
    public FreeCam Cam { get; } = new();
    /// <summary>Vertical field of view in degrees.</summary>
    public float Fov { get; private set; } = 60;
    /// <summary>Exposure change in stops (the scene's exposure × 2^EV).</summary>
    public float Ev { get; private set; }
    public bool OverlayHidden { get; private set; }
    /// <summary>The frame being captured: no overlay at all.</summary>
    public bool Capturing { get; set; }

    private float _flash;
    private string _flashText = "";

    public static string Folder => Path.Combine(Path.GetDirectoryName(Settings.FilePath)!, "Screenshots");

    public void Open(System.Numerics.Vector3 eye, System.Numerics.Vector3 look, float fovDegrees)
    {
        Cam.Place(eye, look);
        (Active, Fov, Ev, OverlayHidden, Capturing, _flash) = (true, Math.Clamp(MathF.Round(fovDegrees), FovMin, FovMax), 0, false, false, 0);
    }

    public void Close() => Active = false;

    /// <summary>Shows where the photo went.</summary>
    public void Saved(string path) => (_flashText, _flash) = ($"SAVED  {Path.GetFileName(path)}", 2.5f);

    /// <summary>One frame: <paramref name="keys"/> (<see cref="MenuKeys"/>: DECIDE photo, BACK leave), the rest straight from <paramref name="input"/>.</summary>
    public Command Update((int X, int Y, bool Ok, bool Back) keys, InputSnapshot input, float dt, Action<string>? sound)
    {
        _flash = MathF.Max(0, _flash - dt);
        var k = input.Keyboard;
        var pad = input.Gamepad;
        bool Down(Key key, GamepadButton b) => k.IsKeyDown(key) || pad.IsConnected && pad.IsButtonDown(b);
        bool P(Key key, GamepadButton b) => k.IsKeyPressed(key) || pad.IsConnected && pad.IsButtonPressed(b);
        if (keys.Back)
        {
            sound?.Invoke("BEEP001");
            return Command.Exit;
        }
        if (keys.Ok)
        {
            sound?.Invoke("SYS006");
            return Command.Capture;
        }
        if (P(Key.H, GamepadButton.X)) OverlayHidden = !OverlayHidden;
        Fov = Math.Clamp(Fov + ((Down(Key.Down, GamepadButton.DpadDown) ? 1 : 0) - (Down(Key.Up, GamepadButton.DpadUp) ? 1 : 0)) * 30 * dt, FovMin, FovMax);
        Ev = Math.Clamp(Ev + ((Down(Key.Right, GamepadButton.DpadRight) ? 1 : 0) - (Down(Key.Left, GamepadButton.DpadLeft) ? 1 : 0)) * 1.5f * dt, EvMin, EvMax);
        Cam.Update(input, dt);
        return Command.None;
    }

    public void Build(Overlay o, int width, int height, Canvas c)
    {
        o.Clear();
        if (!Active || Capturing || OverlayHidden && _flash <= 0) return;
        c.Begin(o, width, height);
        if (!OverlayHidden)
        {
            c.Lettering("PHOTO MODE", c.Left + 26, 52, 30, Canvas.White, Overlay.Rgba(0.72f, 0.73f, 0.75f), 0, 0.18f, false);
            c.Carbon(c.Left + 20, 70, c.Left + 190, 130, 1, false);
            Row(c, "FIELD OF VIEW", $"{Fov:0}°", 92);
            Row(c, "EXPOSURE", (MathF.Round(Ev, 1) + 0f).ToString("+0.0;-0.0", System.Globalization.CultureInfo.InvariantCulture) + " EV", 118);
            Menu.Hint(c, Hints.Pick("WASD/QE: Move  IJKL/MOUSE: Look  UP/DOWN: Zoom  LEFT/RIGHT: Exposure  H: Hide  ENTER: Take photo  ESC: Back",
                "L-STICK: Move  R-STICK: Look  LT/RT: Down/Up  D-PAD UP/DOWN: Zoom  D-PAD LEFT/RIGHT: Exposure  X: Hide  A: Take photo  B: Back",
                $"{Hints.Of(Control.MenuOk)}: Take photo  {Hints.Of(Control.MenuBack)}: Back  (camera on keyboard or pad)"), false);
        }
        if (_flash > 0) c.Text(_flashText, 256, 400, 14, Style.Fade(Canvas.White, MathF.Min(1, _flash * 2)), 0.5f, 0.15f, 0.1f, 0.3f);
    }

    private static void Row(Canvas c, string label, string value, float y)
    {
        c.Text(label, c.Left + 32, y, 10, Overlay.Rgba(0.72f, 0.73f, 0.75f), 0, 0.1f);
        c.Text(value, c.Left + 178, y, 15, Canvas.White, 1, 0.15f, 0, 0.3f);
    }
}
