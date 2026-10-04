using System.Text.Json.Serialization;
using Kansei.Input;

namespace Touge;

/// <summary>Rebindable driving actions (Options → Controls). Fixed keys (F1–F4, M, N, B, T, 1–3) are not in here.</summary>
public enum Control
{
    SteerLeft, SteerRight, Throttle, Brake, Clutch, Handbrake, ShiftUp, ShiftDown,
    Gear1, Gear2, Gear3, Gear4, Gear5, Gear6, GearR,
    ResetCar, Camera, Lights, HighBeam, Pause,
    /// <summary>Menu decide/back from the wheel (keyboard and pad have fixed menu keys).</summary>
    MenuOk, MenuBack,
}

/// <summary>Input device class of a page of the controls screen and of <see cref="DriverInput.Active"/>.</summary>
public enum DeviceKind { Keyboard, Pad, Wheel }

public enum Source { None, Key, PadButton, PadAxis, JoyButton, JoyAxis, JoyHat }

/// <summary>
///     One binding. Key/PadButton/JoyButton: <see cref="Code"/> = key code / button. JoyHat: hat <see cref="Code"/>, direction
///     bit <see cref="Dir"/> (1 up, 2 right, 4 down, 8 left). PadAxis/JoyAxis: axis <see cref="Code"/> read as 0 at
///     <see cref="Rest"/> … 1 at <see cref="Full"/> (raw −1..1). That covers half axes (stick left: rest 0, full −1),
///     separate pedals that rest at one end (Logitech: rest +1, full −1) and combined pedals (one axis, throttle and brake
///     on the two halves). Calibration moves Rest/Full to what the device really reports.
/// </summary>
public sealed record Bind(Source Source, int Code, float Rest = 0, float Full = 1, int Dir = 0)
{
    public static readonly Bind None = new(Source.None, 0);
    public static Bind OfKey(Key k) => new(Source.Key, (int)k);
    public static Bind Pad(GamepadButton b) => new(Source.PadButton, (int)b);
    public static Bind PadAxis(GamepadAxis a, float full) => new(Source.PadAxis, (int)a, 0, full);
    public static Bind Joy(int button) => new(Source.JoyButton, button);
    public static Bind JoyAxis(int axis, float rest, float full) => new(Source.JoyAxis, axis, rest, full);
    public static Bind Hat(int bit, int hat = 0) => new(Source.JoyHat, hat, Dir: bit);

    [JsonIgnore] public DeviceKind DeviceKind => Source switch { Source.Key => DeviceKind.Keyboard, Source.PadButton or Source.PadAxis => DeviceKind.Pad, _ => DeviceKind.Wheel };
    [JsonIgnore] public bool IsAxis => Source is Source.PadAxis or Source.JoyAxis;

    /// <summary>0..1 for an axis position <paramref name="raw"/>.</summary>
    public float AxisValue(float raw) => Full == Rest ? 0 : Math.Clamp((raw - Rest) / (Full - Rest), 0, 1);

    /// <summary>Same physical input (axes: same half), for clearing a duplicate when rebinding.</summary>
    public bool SameInput(Bind o) => Source == o.Source && Code == o.Code && Dir == o.Dir && (!IsAxis || MathF.Sign(Full - Rest) == MathF.Sign(o.Full - o.Rest));

    [JsonIgnore] public string Label => Source switch
    {
        Source.None => "-",
        Source.Key => KeyName(Code),
        Source.PadButton => PadButtonName((GamepadButton)Code),
        Source.PadAxis => (GamepadAxis)Code switch
        {
            GamepadAxis.TriggerLeft => "LT", GamepadAxis.TriggerRight => "RT",
            var a => (a is GamepadAxis.LeftX or GamepadAxis.LeftY ? "L-STICK " : "R-STICK ")
                     + (a is GamepadAxis.LeftX or GamepadAxis.RightX ? Full < Rest ? "LEFT" : "RIGHT" : Full < Rest ? "UP" : "DOWN"),
        },
        Source.JoyButton => $"BUTTON {Code + 1}",
        Source.JoyAxis => $"AXIS {Code + 1} {(Full < Rest ? "-" : "+")}",
        _ => $"HAT{(Code > 0 ? $" {Code + 1}" : "")} {Dir switch { 1 => "UP", 2 => "RIGHT", 4 => "DOWN", _ => "LEFT" }}",
    };

    private static string KeyName(int code)
    {
        var k = (Key)code;
        if (k is >= Key.A and <= Key.Z || k is >= Key.D0 and <= Key.D9) return ((char)char.ToUpperInvariant((char)code)).ToString();
        if (Enum.IsDefined(k))
            return k switch
            {
                Key.LeftShift => "L-SHIFT", Key.RightShift => "R-SHIFT", Key.LeftCtrl => "L-CTRL", Key.RightCtrl => "R-CTRL",
                Key.LeftAlt => "L-ALT", Key.RightAlt => "R-ALT", Key.Escape => "ESC", Key.Backspace => "BACKSPACE",
                _ => k.ToString().ToUpperInvariant(),
            };
        return code is > 32 and < 127 ? ((char)code).ToString() : $"KEY {code}";
    }

    private static string PadButtonName(GamepadButton b) => b switch
    {
        GamepadButton.LeftShoulder => "LB", GamepadButton.RightShoulder => "RB", GamepadButton.LeftStick => "LS", GamepadButton.RightStick => "RS",
        GamepadButton.DpadUp => "D-PAD UP", GamepadButton.DpadDown => "D-PAD DOWN", GamepadButton.DpadLeft => "D-PAD LEFT", GamepadButton.DpadRight => "D-PAD RIGHT",
        _ => b.ToString().ToUpperInvariant(),
    };
}

/// <summary>
///     Bindings (two slots per action and device) and tuning, saved with the <see cref="Ui.Settings"/>. Missing actions (older
///     files) fall back to <see cref="Defaults"/>. Wheel defaults follow the Logitech G29/G920 layout as SDL reports it on
///     Windows (axis 1 wheel, 2 throttle, 3 brake, 4 clutch, pedals resting at +1; paddles buttons 5/6, shifter 13–19, OPTIONS pauses; numbers 1-based as shown); other wheels
///     bind by pressing.
/// </summary>
public sealed class ControlSettings
{
    public Dictionary<Control, Bind[]> Keyboard { get; set; } = Defaults(DeviceKind.Keyboard);
    public Dictionary<Control, Bind[]> Pad { get; set; } = Defaults(DeviceKind.Pad);
    public Dictionary<Control, Bind[]> Wheel { get; set; } = Defaults(DeviceKind.Wheel);

    /// <summary>Joystick used as the wheel (name); null or missing: the first one that is not a pad.</summary>
    public string? WheelName { get; set; }

    public float PadDeadzone { get; set; } = 0.15f;
    /// <summary>Exponent of the stick curve: 1 linear, &gt; 1 finer around the centre.</summary>
    public float PadLinearity { get; set; } = 1;
    /// <summary>Pad rumble 0..1 (kerbs, impacts).</summary>
    public float Rumble { get; set; } = 0.7f;

    /// <summary>Lock-to-lock degrees of the wheel at full axis travel (as set in the wheel's driver).</summary>
    public int Rotation { get; set; } = 900;
    /// <summary>Steering speed: full steering lock at ±<see cref="FullLockDegrees"/>/2.</summary>
    public float Sensitivity { get; set; } = 1;
    public float SteerDeadzone { get; set; }
    public float SteerLinearity { get; set; } = 1;
    public bool InvertSteer { get; set; }
    public float PedalDeadzone { get; set; } = 0.03f;
    public bool InvertThrottle { get; set; }
    public bool InvertBrake { get; set; }
    public bool InvertClutch { get; set; }
    /// <summary>Force feedback 0 (off) … 1.</summary>
    public float FfbStrength { get; set; } = 0.7f;
    /// <summary>Some drivers report the steering axis the other way round.</summary>
    public bool FfbInvert { get; set; }

    /// <summary>Wheel degrees lock to lock that give full steering in the game: the car's 540° rack, faster with <see cref="Sensitivity"/>, at most the wheel's range.</summary>
    public float FullLockDegrees => MathF.Min(Rotation, 540 / MathF.Max(Sensitivity, 0.1f));

    public Dictionary<Control, Bind[]> Page(DeviceKind d) => d switch { DeviceKind.Keyboard => Keyboard, DeviceKind.Pad => Pad, _ => Wheel };

    /// <summary>Both slots of <paramref name="c"/> on <paramref name="d"/>.</summary>
    public Bind[] Get(DeviceKind d, Control c) => Page(d).TryGetValue(c, out var b) && b.Length == 2 ? b : Fallback[(int)d][c];

    /// <summary>Defaults of every action (unbound ones as two empty slots) for files without them; never handed out for writing (<see cref="Set"/> copies).</summary>
    private static readonly Dictionary<Control, Bind[]>[] Fallback = [.. Enum.GetValues<DeviceKind>().Select(d =>
    {
        var all = Defaults(d);
        foreach (var c in Enum.GetValues<Control>()) all.TryAdd(c, [Bind.None, Bind.None]);
        return all;
    })];

    /// <summary>Binds slot <paramref name="slot"/>; the same input is taken away from every other action of that device.</summary>
    public void Set(DeviceKind d, Control c, int slot, Bind bind)
    {
        var page = Page(d);
        foreach (var other in Enum.GetValues<Control>())
        {
            var binds = (Bind[])Get(d, other).Clone();
            for (var i = 0; i < 2; i++)
                if (bind.Source != Source.None && binds[i].SameInput(bind) && (other != c || i != slot))
                    binds[i] = Bind.None;
            if (other == c) binds[slot] = bind;
            page[other] = binds;
        }
    }

    /// <summary>Bindings and tuning of one device back to the defaults.</summary>
    public void Reset(DeviceKind d)
    {
        var fresh = new ControlSettings();
        switch (d)
        {
            case DeviceKind.Keyboard: Keyboard = fresh.Keyboard; break;
            case DeviceKind.Pad: (Pad, PadDeadzone, PadLinearity, Rumble) = (fresh.Pad, fresh.PadDeadzone, fresh.PadLinearity, fresh.Rumble); break;
            default:
                (Wheel, Rotation, Sensitivity, SteerDeadzone, SteerLinearity, InvertSteer) = (fresh.Wheel, fresh.Rotation, fresh.Sensitivity, fresh.SteerDeadzone, fresh.SteerLinearity, false);
                (PedalDeadzone, InvertThrottle, InvertBrake, InvertClutch, FfbStrength, FfbInvert) = (fresh.PedalDeadzone, false, false, false, fresh.FfbStrength, false);
                break;
        }
    }

    /// <summary>Default bindings of a device: the original keyboard/pad layout of this remake, a G29-style wheel.</summary>
    public static Dictionary<Control, Bind[]> Defaults(DeviceKind d)
    {
        Bind[] Two(Bind a, Bind? b = null) => [a, b ?? Bind.None];
        return d switch
        {
            DeviceKind.Keyboard => new()
            {
                [Control.SteerLeft] = Two(Bind.OfKey(Key.A), Bind.OfKey(Key.Left)), [Control.SteerRight] = Two(Bind.OfKey(Key.D), Bind.OfKey(Key.Right)),
                [Control.Throttle] = Two(Bind.OfKey(Key.W), Bind.OfKey(Key.Up)), [Control.Brake] = Two(Bind.OfKey(Key.S), Bind.OfKey(Key.Down)),
                [Control.Clutch] = Two(Bind.OfKey(Key.Q)), [Control.Handbrake] = Two(Bind.OfKey(Key.Space)),
                [Control.ShiftUp] = Two(Bind.OfKey(Key.LeftShift), Bind.OfKey(Key.RightShift)), [Control.ShiftDown] = Two(Bind.OfKey(Key.LeftCtrl), Bind.OfKey(Key.RightCtrl)),
                [Control.ResetCar] = Two(Bind.OfKey(Key.R)), [Control.Camera] = Two(Bind.OfKey(Key.C)), [Control.Lights] = Two(Bind.OfKey(Key.L)),
                [Control.HighBeam] = Two(Bind.OfKey(Key.H)), [Control.Pause] = Two(Bind.OfKey(Key.Escape)),
            },
            DeviceKind.Pad => new()
            {
                [Control.SteerLeft] = Two(Bind.PadAxis(GamepadAxis.LeftX, -1)), [Control.SteerRight] = Two(Bind.PadAxis(GamepadAxis.LeftX, 1)),
                [Control.Throttle] = Two(Bind.PadAxis(GamepadAxis.TriggerRight, 1)), [Control.Brake] = Two(Bind.PadAxis(GamepadAxis.TriggerLeft, 1)),
                [Control.Handbrake] = Two(Bind.Pad(GamepadButton.A)), [Control.ShiftUp] = Two(Bind.Pad(GamepadButton.RightShoulder)),
                [Control.ShiftDown] = Two(Bind.Pad(GamepadButton.LeftShoulder)), [Control.ResetCar] = Two(Bind.Pad(GamepadButton.Y)),
                [Control.Camera] = Two(Bind.Pad(GamepadButton.Back)), [Control.Lights] = Two(Bind.Pad(GamepadButton.DpadUp)),
                [Control.HighBeam] = Two(Bind.Pad(GamepadButton.DpadDown)), [Control.Pause] = Two(Bind.Pad(GamepadButton.Start)),
            },
            _ => new()
            {
                [Control.SteerLeft] = Two(Bind.JoyAxis(0, 0, -1)), [Control.SteerRight] = Two(Bind.JoyAxis(0, 0, 1)),
                [Control.Throttle] = Two(Bind.JoyAxis(1, 1, -1)), [Control.Brake] = Two(Bind.JoyAxis(2, 1, -1)), [Control.Clutch] = Two(Bind.JoyAxis(3, 1, -1)),
                [Control.ShiftUp] = Two(Bind.Joy(4)), [Control.ShiftDown] = Two(Bind.Joy(5)),
                [Control.Gear1] = Two(Bind.Joy(12)), [Control.Gear2] = Two(Bind.Joy(13)), [Control.Gear3] = Two(Bind.Joy(14)), [Control.Gear4] = Two(Bind.Joy(15)),
                [Control.Gear5] = Two(Bind.Joy(16)), [Control.Gear6] = Two(Bind.Joy(17)), [Control.GearR] = Two(Bind.Joy(18)),
                [Control.ResetCar] = Two(Bind.Joy(3)), [Control.Camera] = Two(Bind.Joy(6)), [Control.Lights] = Two(Bind.Hat(1)), [Control.HighBeam] = Two(Bind.Hat(4)),
                [Control.Pause] = Two(Bind.Joy(9)), [Control.MenuOk] = Two(Bind.Joy(0)), [Control.MenuBack] = Two(Bind.Joy(2), Bind.Joy(1)),
            },
        };
    }
}
