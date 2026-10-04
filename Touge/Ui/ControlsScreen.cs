using System.Numerics;
using Kansei.Graphics;
using Kansei.Input;

namespace Touge.Ui;

/// <summary>
///     Options → CONTROLS, in the style of the options screen: three chrome tabs (KEYBOARD / GAMEPAD / WHEEL), a list of
///     rows – tuning values and every action with two binding slots – a live panel (wheel angle, pedal bars, raw axes, force)
///     and a help box. Bind by pressing: DECIDE on a slot, then the key, button, hat or axis (moving an axis past half its
///     travel; the start position becomes its rest). DELETE / pad X clears a slot, ESC cancels, 6 s without input as well.
///     Wheel calibration: centre + release (rest of every axis), then lock to lock and every pedal down (the extremes).
/// </summary>
public sealed class ControlsScreen(ControlSettings cfg, InputSnapshot input, DriverInput driver)
{
    private sealed record Row(string Label, string Help, Control? Control = null, Func<string>? Value = null, Action<int>? Change = null, Action? Activate = null);

    private static readonly string[] Tabs = ["KEYBOARD", "GAMEPAD", "WHEEL"];
    private static readonly string[] PadAxisNames = ["L-STICK X", "L-STICK Y", "R-STICK X", "R-STICK Y", "LT", "RT"];
    private const int Visible = 12;
    private const float RowH = 22, Top = 94, CaptureTimeout = 6;

    private DeviceKind _page;
    private int _row = -1, _slot, _scroll, _calib;
    private float _capture = -1, _test = -1, _clock;
    private float[] _base = [], _min = [], _max = [];
    private Row[] _rows = [];

    public DeviceKind Page => _page;
    /// <summary>Force of the FFB test (DECIDE on FORCE FEEDBACK): 0.5 s right, 0.5 s left; the game sends it while the menus hold the race.</summary>
    public float TestForce => _test < 0 ? 0 : _test < 0.5f ? 0.5f : -0.5f;
    public bool Capturing => _capture >= 0;

    public void Open(DeviceKind page = DeviceKind.Keyboard) => (_page, _row, _slot, _scroll, _capture, _calib, _test) = (page, -1, 0, 0, -1, 0, -1);

    private static string Pct(float v) => $"{MathF.Round(v * 100):0}%";
    private static float Step(float v, int step, float by, float lo, float hi) => Math.Clamp(MathF.Round((v + step * by) / by) * by, lo, hi);
    private static string OnOff(bool b) => b ? "ON" : "OFF";
    private static string Curve(float e) => MathF.Abs(e - 1) < 0.01f ? "LINEAR" : FormattableString.Invariant($"{e:0.0#}");

    private static string Name(Control c) => c switch
    {
        Control.SteerLeft => "STEER LEFT", Control.SteerRight => "STEER RIGHT", Control.ShiftUp => "SHIFT UP", Control.ShiftDown => "SHIFT DOWN",
        Control.GearR => "GEAR R", >= Control.Gear1 and <= Control.Gear6 => $"GEAR {c - Control.Gear1 + 1}", Control.ResetCar => "BACK TO ROAD",
        Control.HighBeam => "HIGH BEAM", Control.MenuOk => "MENU DECIDE", Control.MenuBack => "MENU BACK", _ => c.ToString().ToUpperInvariant(),
    };

    private static string BindHelp(Control c) => c switch
    {
        Control.SteerLeft or Control.SteerRight => "Steering. A wheel axis bound here steers 1:1, without the pad/keyboard helpers.",
        Control.Throttle or Control.Brake or Control.Clutch => "Pedal: an axis gives the full range (rest to full as bound or calibrated), a key full travel.",
        >= Control.Gear1 and <= Control.GearR => "H-shifter (MT): once used, no gear held is neutral. SHIFT UP/DOWN switches back to sequential.",
        Control.MenuOk or Control.MenuBack => "Menus from the wheel; the hat moves the cursor, the shift paddles go left/right.",
        _ => "",
    };

    private Row[] Build()
    {
        var rows = new List<Row>();
        var s = cfg;
        switch (_page)
        {
            case DeviceKind.Pad:
                rows.Add(new("STICK DEADZONE", "Stick travel ignored around the centre (also the triggers).", Value: () => Pct(s.PadDeadzone), Change: d => s.PadDeadzone = Step(s.PadDeadzone, d, 0.05f, 0, 0.4f)));
                rows.Add(new("STICK CURVE", "LINEAR, or finer steering around the centre.", Value: () => Curve(s.PadLinearity), Change: d => s.PadLinearity = Step(s.PadLinearity, d, 0.25f, 1, 3)));
                rows.Add(new("RUMBLE", "Rumble on kerbs, gutters and grass, and when hitting a wall.", Value: () => s.Rumble <= 0 ? "OFF" : Pct(s.Rumble), Change: d => s.Rumble = Step(s.Rumble, d, 0.1f, 0, 1)));
                break;
            case DeviceKind.Wheel:
                rows.Add(new("DEVICE", "The joystick used as the wheel (wheels, pedal sets and shifters show up here).", Value: () => driver.Wheel?.Name.ToUpperInvariant() ?? "NONE CONNECTED", Change: CycleWheel));
                rows.Add(new("CALIBRATE", "Measures the rest position and full travel of every bound axis.", Value: () => "START", Activate: () => (_calib, _base) = (1, Axes())));
                rows.Add(new("ROTATION", "Lock-to-lock range of the wheel as set in its driver (at full axis travel).", Value: () => $"{s.Rotation}°", Change: d => s.Rotation = Math.Clamp(s.Rotation + 90 * d, 180, 1080)));
                rows.Add(new("SENSITIVITY", "Full steering lock at the wheel angle shown in the live panel.", Value: () => Pct(s.Sensitivity), Change: d => s.Sensitivity = Step(s.Sensitivity, d, 0.1f, 0.5f, 2)));
                rows.Add(new("STEER DEADZONE", "Wheel travel ignored around the centre.", Value: () => Pct(s.SteerDeadzone), Change: d => s.SteerDeadzone = Step(s.SteerDeadzone, d, 0.01f, 0, 0.2f)));
                rows.Add(new("STEER CURVE", "LINEAR (recommended for a wheel), or finer around the centre.", Value: () => Curve(s.SteerLinearity), Change: d => s.SteerLinearity = Step(s.SteerLinearity, d, 0.25f, 1, 3)));
                rows.Add(new("INVERT STEERING", "Swaps left and right of the steering axis.", Value: () => OnOff(s.InvertSteer), Change: _ => s.InvertSteer = !s.InvertSteer));
                rows.Add(new("PEDAL DEADZONE", "Pedal travel ignored at rest (worn pedals that never read zero).", Value: () => Pct(s.PedalDeadzone), Change: d => s.PedalDeadzone = Step(s.PedalDeadzone, d, 0.01f, 0, 0.2f)));
                rows.Add(new("INVERT THROTTLE", "Reads the throttle axis the other way round.", Value: () => OnOff(s.InvertThrottle), Change: _ => s.InvertThrottle = !s.InvertThrottle));
                rows.Add(new("INVERT BRAKE", "Reads the brake axis the other way round.", Value: () => OnOff(s.InvertBrake), Change: _ => s.InvertBrake = !s.InvertBrake));
                rows.Add(new("INVERT CLUTCH", "Reads the clutch axis the other way round.", Value: () => OnOff(s.InvertClutch), Change: _ => s.InvertClutch = !s.InvertClutch));
                rows.Add(new("FORCE FEEDBACK", "Tyre forces, kerbs and impacts on the wheel.  DECIDE: test (right, then left).",
                    Value: () => s.FfbStrength <= 0 ? "OFF" : Pct(s.FfbStrength), Change: d => s.FfbStrength = Step(s.FfbStrength, d, 0.1f, 0, 1), Activate: () => _test = 0));
                rows.Add(new("FFB DIRECTION", "REVERSED if the test pushes left first.", Value: () => s.FfbInvert ? "REVERSED" : "NORMAL", Change: _ => s.FfbInvert = !s.FfbInvert));
                break;
        }
        foreach (var c in Enum.GetValues<Control>())
            if (_page == DeviceKind.Wheel || c is not (Control.MenuOk or Control.MenuBack))
                rows.Add(new(Name(c), BindHelp(c), c));
        rows.Add(new("RESET", $"All {Tabs[(int)_page].ToLowerInvariant()} bindings and settings back to the defaults.", Value: () => "DEFAULTS", Activate: () => s.Reset(_page)));
        return [.. rows];
    }

    private void CycleWheel(int d)
    {
        var all = input.Joysticks;
        if (all.Count == 0) return;
        var i = driver.Wheel == null ? -1 : all.ToList().IndexOf(driver.Wheel);
        cfg.WheelName = all[((i + d) % all.Count + all.Count) % all.Count].Name;
    }

    private float[] Axes()
    {
        var w = driver.Wheel;
        return w == null ? [] : [.. Enumerable.Range(0, w.AxisCount).Select(w.Axis)];
    }

    private static float Snap(float v) => MathF.Abs(v) > 0.7f ? MathF.Sign(v) : 0;

    /// <summary>One frame; true = leave the screen (BACK).</summary>
    public bool Update((int X, int Y, bool Ok, bool Back) k, float dt, Action<string>? sound)
    {
        _clock += dt;
        _rows = Build();
        if (_test >= 0 && (_test += dt) > 1) _test = -1;
        if (_capture >= 0)
        {
            Capture(dt, sound);
            return false;
        }
        if (_calib > 0)
        {
            var now = Axes();
            if (_calib == 2 && now.Length == _min.Length)
                for (var i = 0; i < now.Length; i++) (_min[i], _max[i]) = (MathF.Min(_min[i], now[i]), MathF.Max(_max[i], now[i]));
            if (k.Back)
            {
                (_calib, _test) = (0, -1);
                sound?.Invoke("BEEP001");
            }
            else if (k.Ok && _calib == 1)
            {
                (_calib, _base, _min, _max) = (2, now, (float[])now.Clone(), (float[])now.Clone());
                sound?.Invoke("SYS006");
            }
            else if (k.Ok)
            {
                ApplyCalibration();
                _calib = 0;
                sound?.Invoke("SYS006");
            }
            return false;
        }
        if (k.Back) return true;
        if (k.Y != 0)
        {
            _row = Math.Clamp(_row + k.Y, -1, _rows.Length - 1);
            sound?.Invoke("SYS005");
            if (_row >= 0) _scroll = Math.Clamp(_scroll, _row - Visible + 1, _row);
            return false;
        }
        if (_row < 0)
        {
            if (k.X != 0)
            {
                _page = (DeviceKind)(((int)_page + k.X + 3) % 3);
                sound?.Invoke("SYS005");
            }
            else if (k.Ok)
            {
                _row = 0;
                sound?.Invoke("SYS005");
            }
            return false;
        }
        var row = _rows[_row];
        if (row.Control is { } c)
        {
            if (k.X != 0 && (_slot + k.X) is 0 or 1)
            {
                _slot += k.X;
                sound?.Invoke("SYS005");
            }
            else if (k.Ok)
            {
                (_capture, _base) = (0, _page == DeviceKind.Pad ? [.. Enum.GetValues<GamepadAxis>().Select(input.Gamepad.RawAxis)] : Axes());
                sound?.Invoke("SYS006");
            }
            else if (input.Keyboard.IsKeyPressed(Key.Delete) || input.Gamepad.IsButtonPressed(GamepadButton.X))
            {
                cfg.Set(_page, c, _slot, Bind.None);
                sound?.Invoke("BEEP001");
            }
        }
        else if (k.X != 0 && row.Change != null)
        {
            row.Change(k.X);
            sound?.Invoke("SYS005");
        }
        else if (k.Ok && (row.Activate ?? (row.Change != null ? () => row.Change(1) : null)) is { } act)
        {
            act();
            sound?.Invoke("SYS006");
        }
        return false;
    }

    /// <summary>Waits for the new binding of the selected slot (input of the page's device only; the starting frame is skipped).</summary>
    private void Capture(float dt, Action<string>? sound)
    {
        var first = _capture == 0;
        _capture += dt;
        if (input.Keyboard.IsKeyPressed(Key.Escape) || _capture > CaptureTimeout)
        {
            _capture = -1;
            sound?.Invoke("BEEP001");
            return;
        }
        if (first || Detect() is not { } bind) return;
        cfg.Set(_page, _rows[_row].Control!.Value, _slot, bind);
        _capture = -1;
        sound?.Invoke("SYS006");
    }

    /// <summary>The input just used on the page's device, as a binding (null: none yet).</summary>
    public Bind? Detect()
    {
        switch (_page)
        {
            case DeviceKind.Keyboard:
                foreach (var key in input.Keyboard.PressedThisFrame) return new Bind(Source.Key, (int)key);
                return null;
            case DeviceKind.Pad:
                foreach (var b in input.Gamepad.PressedThisFrame) return new Bind(Source.PadButton, (int)b);
                var axes = Enum.GetValues<GamepadAxis>();
                for (var i = 0; i < axes.Length && i < _base.Length; i++)
                {
                    var d = input.Gamepad.RawAxis(axes[i]) - _base[i];
                    if (MathF.Abs(d) > 0.5f) return Bind.PadAxis(axes[i], MathF.Sign(d)) with { Rest = Snap(_base[i]) };
                }
                return null;
            default:
                var w = driver.Wheel;
                if (w == null) return null;
                for (var i = 0; i < w.ButtonCount; i++)
                    if (w.ButtonPressed(i)) return Bind.Joy(i);
                for (var h = 0; h < w.HatCount; h++)
                    foreach (var bit in (ReadOnlySpan<int>)[1, 2, 4, 8])
                        if (w.HatPressed(h, bit)) return Bind.Hat(bit, h);
                for (var i = 0; i < w.AxisCount && i < _base.Length; i++)
                {
                    var d = w.Axis(i) - _base[i];
                    if (MathF.Abs(d) <= 0.5f) continue;
                    var rest = Snap(_base[i]);
                    return Bind.JoyAxis(i, rest, rest != 0 ? -rest : MathF.Sign(d));
                }
                return null;
        }
    }

    /// <summary>Rest and full of every bound wheel axis from the two calibration steps (axes that hardly moved keep theirs).</summary>
    private void ApplyCalibration()
    {
        foreach (var c in Enum.GetValues<Control>())
        {
            var binds = cfg.Get(DeviceKind.Wheel, c);
            for (var slot = 0; slot < 2; slot++)
            {
                var b = binds[slot];
                if (b.Source != Source.JoyAxis || b.Code >= _base.Length) continue;
                var full = b.Full < b.Rest ? _min[b.Code] : _max[b.Code];
                if (MathF.Abs(full - _base[b.Code]) > 0.25f) cfg.Set(DeviceKind.Wheel, c, slot, b with { Rest = _base[b.Code], Full = full });
            }
        }
    }

    // ---------------------------------------------------------------- drawing

    public void Draw(Canvas c, float theta)
    {
        var grey = Overlay.Rgba(0.72f, 0.73f, 0.75f);
        if (_rows.Length == 0) _rows = Build();
        for (var i = 0; i < Tabs.Length; i++)
        {
            var on = (int)_page == i;
            c.Plate(36 + i * 150, 64, 140, 24, on ? 1 : 0.5f);
            c.Text(Tabs[i], 106 + i * 150, 81, 13, on ? Canvas.Shade(0.08f, 0.08f, 0.08f, 1) : Overlay.Rgba(0.15f, 0.15f, 0.15f, 0.8f), 0.5f, 0.15f);
        }
        if (_row < 0) c.Glow(32 + (int)_page * 150, 60, 180 + (int)_page * 150, 92, Canvas.Pulse(theta));

        for (var v = 0; v < Visible && _scroll + v < _rows.Length; v++)
        {
            var i = _scroll + v;
            var row = _rows[i];
            var y = Top + v * RowH;
            Vector2 min = Vector2.Round(c.P(20, y)), max = Vector2.Round(c.P(136, y + RowH - 3));
            c.O.Rect(min, max, Overlay.Rgba(0.5f, 0.51f, 0.53f));
            c.O.RectGradient(min + new Vector2(1.5f, 1.5f) * c.S, max - new Vector2(1.5f, 1.5f) * c.S, Overlay.Rgba(0.24f, 0.25f, 0.26f), Overlay.Rgba(0.1f, 0.1f, 0.11f));
            c.Fit(row.Label, 78, y + 14, 106, 0.5f, Canvas.White, 0.1f, 0, 11);
            if (row.Control is { } ctl)
            {
                var binds = cfg.Get(_page, ctl);
                for (var s = 0; s < 2; s++)
                {
                    var x = 140 + s * 96;
                    Cell(c, x, y, 92);
                    var capturing = _capture >= 0 && i == _row && s == _slot;
                    var text = capturing ? (_clock * 2 % 1 < 0.6f ? _page switch { DeviceKind.Keyboard => "PRESS A KEY", DeviceKind.Pad => "PRESS / MOVE", _ => "PRESS / MOVE" } : "")
                        : binds[s].Label;
                    c.Fit(text, x + 46, y + 14, 84, 0.5f, capturing ? Canvas.Yellow : binds[s].Source == Source.None ? Overlay.Rgba(1, 1, 1, 0.35f) : Canvas.White, 0.1f, 0, 11);
                }
            }
            else
            {
                Cell(c, 140, y, 188);
                c.Fit(row.Value?.Invoke() ?? "", 234, y + 14, 176, 0.5f, Canvas.White, 0.1f, 0, 11);
            }
        }
        if (_row >= 0 && _row - _scroll is >= 0 and < Visible)
        {
            var y = Top + (_row - _scroll) * RowH;
            var (x0, x1) = _rows[_row].Control != null ? (138 + _slot * 96, 234 + _slot * 96) : (138f, 330f);
            c.Glow(x0, y - 2, x1, y + RowH - 1, Canvas.Pulse(theta));
        }
        if (_scroll > 0) c.Arrow(170, Top - 1, 186, Top - 1, 178, Top - 7);
        if (_scroll + Visible < _rows.Length) c.Arrow(170, Top + Visible * RowH, 186, Top + Visible * RowH, 178, Top + Visible * RowH + 6);

        LivePanel(c, grey);

        c.Carbon(20, 364, 496, 422, 1, false);
        var lines = HelpLines();
        for (var i = 0; i < lines.Length; i++) c.Fit(lines[i], 32, 384 + i * 17, 452, 0, i == 0 && (_capture >= 0 || _calib > 0) ? Canvas.Yellow : Canvas.White, 0.12f, 0, 11.5f);
    }

    private string[] HelpLines()
    {
        if (_capture >= 0)
            return [$"{Name(_rows[_row].Control!.Value)}, slot {_slot + 1}: " + _page switch
            {
                DeviceKind.Keyboard => "press the new key.",
                DeviceKind.Pad => "press a button or move a stick/trigger.",
                _ => "press a button or the hat, or move an axis (wheel, pedal) past half its travel.",
            }, $"ESC: cancel ({MathF.Ceiling(CaptureTimeout - _capture):0} s)"];
        if (_calib == 1) return ["CALIBRATE 1/2: centre the wheel and release every pedal, then DECIDE.", "BACK: cancel"];
        if (_calib == 2) return ["CALIBRATE 2/2: turn the wheel to both locks and press every pedal fully, then DECIDE.", "BACK: cancel"];
        if (_row < 0) return ["LEFT/RIGHT: device    DOWN: settings and bindings    BACK: options", ""];
        var row = _rows[_row];
        var nav = row.Control != null ? "DECIDE: bind    LEFT/RIGHT: slot    DELETE / pad X: clear    BACK: options" : "LEFT/RIGHT: change    BACK: options";
        if (_page == DeviceKind.Keyboard && row.Help == "")
            return [nav, "Fixed: F1 free camera, F2 graphics, F3 music, F4 HUD, M track, N map, T AT/MT, B direction, 1-3 car/colour."];
        return [nav, row.Help];
    }

    private static void Cell(Canvas c, float x, float y, float w)
    {
        Vector2 min = Vector2.Round(c.P(x, y)), max = Vector2.Round(c.P(x + w, y + RowH - 3));
        c.O.Rect(min, max, Overlay.Rgba(0.04f, 0.04f, 0.05f, 0.9f));
        c.O.Line(new Vector2(min.X, max.Y), new Vector2(max.X, max.Y), 1, Overlay.Rgba(1, 1, 1, 0.18f));
    }

    /// <summary>Right panel: device, steering wheel turned as the game reads it, pedal bars, raw axes (wheel) and the force.</summary>
    private void LivePanel(Canvas c, uint grey)
    {
        c.Carbon(338, 94, 496, 358, 1, false);
        var o = c.O;
        var w = driver.Wheel;
        var name = _page switch
        {
            DeviceKind.Keyboard => "KEYBOARD",
            DeviceKind.Pad => input.Gamepad.IsConnected ? input.ActiveName?.ToUpperInvariant() ?? "GAMEPAD" : "NO GAMEPAD CONNECTED",
            _ => w == null ? "NO WHEEL CONNECTED" : w.Name.ToUpperInvariant(),
        };
        c.Fit(name, 417, 112, 140, 0.5f, Canvas.White, 0.1f, 0, 11);
        if (_page == DeviceKind.Wheel && w != null)
            c.Text(w.HasForceFeedback ? "FORCE FEEDBACK" : "NO FORCE FEEDBACK", 417, 124, 8.5f, w.HasForceFeedback ? Style.Green : grey, 0.5f, 0.1f);

        // steering wheel: on the wheel page at the real wheel angle, else the game's steering × half the lock
        var deg = _page == DeviceKind.Wheel ? (driver.Analog(input, DeviceKind.Wheel, Control.SteerRight) - driver.Analog(input, DeviceKind.Wheel, Control.SteerLeft)) * cfg.Rotation / 2
            : driver.Steer * 90;
        var centre = c.P(390, 176);
        var r = 34 * c.S;
        o.Ring(centre, r, 7 * c.S, Overlay.Rgba(0.16f, 0.16f, 0.17f));
        o.Ring(centre, r, 2 * c.S, Overlay.Rgba(0.45f, 0.46f, 0.48f));
        var a = deg * MathF.PI / 180;
        Vector2 Dir(float ang) => new(MathF.Sin(ang), -MathF.Cos(ang));
        foreach (var spoke in (ReadOnlySpan<float>)[MathF.PI / 2, -MathF.PI / 2, MathF.PI])
            o.Line(centre, centre + Dir(a + spoke) * r, 5 * c.S, Overlay.Rgba(0.22f, 0.22f, 0.24f));
        o.Line(centre + Dir(a) * (r - 5 * c.S), centre + Dir(a) * (r + 3 * c.S), 4 * c.S, Overlay.Rgba(1, 0.75f, 0.1f)); // top marker
        o.Disc(centre, 9 * c.S, Overlay.Rgba(0.1f, 0.1f, 0.11f));
        c.Text($"{deg:+0;-0;0}°", 390, 226, 12, Canvas.White, 0.5f, 0.1f);
        if (_page == DeviceKind.Wheel) c.Text($"FULL LOCK AT {cfg.FullLockDegrees / 2:0}° EACH WAY", 390, 238, 8.5f, grey, 0.5f, 0.1f);

        // pedal bars and the handbrake lamp (what the car gets, all devices)
        (string L, float V)[] bars = [("THR", driver.Throttle), ("BRK", driver.Brake), ("CLU", driver.Clutch)];
        for (var i = 0; i < bars.Length; i++)
        {
            float x = 438 + i * 18, y0 = 146, y1 = 214;
            o.Rect(Vector2.Round(c.P(x, y0)), Vector2.Round(c.P(x + 12, y1)), Overlay.Rgba(0.2f, 0.2f, 0.22f));
            o.Rect(Vector2.Round(c.P(x, y1 - (y1 - y0) * bars[i].V)), Vector2.Round(c.P(x + 12, y1)), i == 0 ? Style.Green : i == 1 ? Style.Red : Overlay.Rgba(0.3f, 0.55f, 1));
            c.Text(bars[i].L, x + 6, 226, 8, grey, 0.5f, 0.1f);
        }
        o.Disc(c.P(484, 236), 4 * c.S, driver.Handbrake ? Style.Red : Overlay.Rgba(0.2f, 0.2f, 0.22f));
        c.Text("HB", 474, 239, 8, grey, 1, 0.1f);

        // steering bar
        float sx0 = 352, sx1 = 482, sy = 252;
        o.Rect(Vector2.Round(c.P(sx0, sy)), Vector2.Round(c.P(sx1, sy + 6)), Overlay.Rgba(0.2f, 0.2f, 0.22f));
        var mid = (sx0 + sx1) / 2;
        var sv = mid + driver.Steer * (sx1 - sx0) / 2;
        o.Rect(Vector2.Round(c.P(MathF.Min(mid, sv), sy)), Vector2.Round(c.P(MathF.Max(mid, sv), sy + 6)), Overlay.Rgba(1, 0.75f, 0.1f));
        c.Text(FormattableString.Invariant($"STEER {driver.Steer:+0.00;-0.00;0.00}   {(driver.HGear is { } g ? g < 0 ? "GEAR R" : g == 0 ? "GEAR N" : $"GEAR {g}" : "")}"), 352, 272, 8.5f, grey, 0, 0.1f);

        if (_page != DeviceKind.Wheel || w == null)
        {
            if (_page == DeviceKind.Pad)
                for (var i = 0; i < 6; i++)
                    AxisBar(c, i, PadAxisNames[i], input.Gamepad.RawAxis(Enum.GetValues<GamepadAxis>()[i]), grey);
            return;
        }
        for (var i = 0; i < Math.Min(w.AxisCount, 6); i++) AxisBar(c, i, $"AXIS {i + 1}", w.Axis(i), grey);
        var pressed = string.Join(" ", Enumerable.Range(0, w.ButtonCount).Where(w.Button).Select(b => (b + 1).ToString()));
        c.Fit(FormattableString.Invariant($"BUTTONS {(pressed == "" ? "-" : pressed)}   HAT {w.Hat(0)}   FORCE {w.Force:+0.00;-0.00;0.00}"), 352, 352, 132, 0, grey, 0.1f, 0, 8.5f);
    }

    /// <summary>Raw axis −1..1 as a centred bar with its number (row <paramref name="i"/> under the steering bar).</summary>
    private static void AxisBar(Canvas c, int i, string label, float v, uint grey)
    {
        var y = 284 + i * 10;
        c.Fit(label, 352, y + 6, 46, 0, grey, 0.1f, 0, 7.5f);
        float x0 = 402, x1 = 482, mid = (x0 + x1) / 2, xv = mid + v * (x1 - x0) / 2;
        c.O.Rect(Vector2.Round(c.P(x0, y)), Vector2.Round(c.P(x1, y + 6)), Overlay.Rgba(0.2f, 0.2f, 0.22f));
        c.O.Rect(Vector2.Round(c.P(MathF.Min(mid, xv), y)), Vector2.Round(c.P(MathF.Max(mid, xv), y + 6)), Overlay.Rgba(0.55f, 0.75f, 1));
    }
}
