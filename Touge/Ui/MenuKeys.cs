using Kansei.Input;

namespace Touge.Ui;

/// <summary>
///     Menu navigation: keyboard (arrows/WASD with key repeat, Enter/Space, Esc/Backspace), every connected pad (D-pad or
///     left stick held, A/Start, B) and the wheel (hat, shift paddles, MENU DECIDE/BACK bindings). Pad and wheel directions
///     repeat with the keyboard's timing (<see cref="HoldRepeat"/>). Also tells <see cref="Hints"/> which device was used last.
/// </summary>
public sealed class MenuKeys
{
    private readonly DirRepeat _dirs = new();
    private bool _up, _down;

    /// <summary>The last <see cref="Read"/>'s Y is key repeat: that direction was already held the frame before (not a new press).</summary>
    public bool RepeatY { get; private set; }

    /// <summary>Wheel in the menus: hat = arrows, shift paddles = left/right, MENU DECIDE/BACK bindings (Options → Controls).</summary>
    public ControlSettings? Wheel { get; set; }

    /// <summary>The pads one by one; the merged <see cref="InputSnapshot.Gamepad"/> where none is listed (device-free test snapshots).</summary>
    public static IReadOnlyList<GamepadState> PadsOf(InputSnapshot input) =>
        input.Pads.Count > 0 ? input.Pads : input.Gamepad.IsConnected ? [input.Gamepad] : [];

    public (int X, int Y, bool Ok, bool Back) Read(InputSnapshot input, float dt)
    {
        var k = input.Keyboard;
        int x = 0, y = 0;
        if (k.IsKeyRepeating(Key.Up, dt) || k.IsKeyRepeating(Key.W, dt)) y--;
        if (k.IsKeyRepeating(Key.Down, dt) || k.IsKeyRepeating(Key.S, dt)) y++;
        if (k.IsKeyRepeating(Key.Left, dt) || k.IsKeyRepeating(Key.A, dt)) x--;
        if (k.IsKeyRepeating(Key.Right, dt) || k.IsKeyRepeating(Key.D, dt)) x++;
        var ok = k.IsKeyPressed(Key.Enter) || k.IsKeyPressed(Key.Space);
        var back = k.IsKeyPressed(Key.Escape) || k.IsKeyPressed(Key.Backspace);
        var pads = PadsOf(input);
        bool up = false, down = false, left = false, right = false;
        foreach (var pad in pads)
        {
            var (u, d, l, r) = DirRepeat.Held(pad);
            (up, down, left, right) = (up | u, down | d, left | l, right | r);
            ok |= pad.IsButtonPressed(GamepadButton.A) || pad.IsButtonPressed(GamepadButton.Start);
            back |= pad.IsButtonPressed(GamepadButton.B);
        }
        var w = Wheel != null ? DriverInput.FindWheel(input, Wheel.WheelName) : null;
        if (w != null)
        {
            bool Down(Control c) => Wheel!.Get(DeviceKind.Wheel, c).Any(b => DriverInput.Value(b, input, w, null) > 0.5f);
            bool P(Control c) => Wheel!.Get(DeviceKind.Wheel, c).Any(b => DriverInput.Pressed(b, w));
            var hat = w.Hat(0);
            (up, down) = (up | (hat & 1) != 0, down | (hat & 4) != 0);
            (left, right) = (left | (hat & 8) != 0 || Down(Control.ShiftDown), right | (hat & 2) != 0 || Down(Control.ShiftUp));
            ok |= P(Control.MenuOk);
            back |= P(Control.MenuBack);
        }
        var (dx, dy) = _dirs.Step(up, down, left, right, dt);
        Hints.Track(input, pads, w);
        var sy = Math.Sign(y + dy);
        RepeatY = sy < 0 ? _up : sy > 0 && _down;
        (_up, _down) = (up || k.IsKeyDown(Key.Up) || k.IsKeyDown(Key.W), down || k.IsKeyDown(Key.Down) || k.IsKeyDown(Key.S));
        return (Math.Sign(x + dx), sy, ok, back);
    }
}

/// <summary>Four held directions → menu steps −1/0/+1 per axis, each repeating as a held key does.</summary>
internal sealed class DirRepeat
{
    private const float Stick = 0.6f;
    private readonly HoldRepeat _up = new(), _down = new(), _left = new(), _right = new();

    /// <summary>Directions <paramref name="pad"/> holds: D-pad or left stick past 60 %.</summary>
    public static (bool Up, bool Down, bool Left, bool Right) Held(GamepadState pad)
    {
        float sx = pad.GetAxis(GamepadAxis.LeftX), sy = pad.GetAxis(GamepadAxis.LeftY);
        return (pad.IsButtonDown(GamepadButton.DpadUp) || sy < -Stick, pad.IsButtonDown(GamepadButton.DpadDown) || sy > Stick,
            pad.IsButtonDown(GamepadButton.DpadLeft) || sx < -Stick, pad.IsButtonDown(GamepadButton.DpadRight) || sx > Stick);
    }

    public (int X, int Y) Step(bool up, bool down, bool left, bool right, float dt) =>
        ((_right.Update(right, dt) ? 1 : 0) - (_left.Update(left, dt) ? 1 : 0), (_down.Update(down, dt) ? 1 : 0) - (_up.Update(up, dt) ? 1 : 0));
}
