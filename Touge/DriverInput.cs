using Kansei.Input;
using Kansei.Physics;

namespace Touge;

/// <summary>
///     Keyboard, pad and wheel through the bindings of <see cref="ControlSettings"/> → driver input, once per frame
///     (<see cref="Update"/>). Pedals: the most pressed of all devices. Steering from the device used last
///     (<see cref="Active"/>): the wheel 1:1 and unsmoothed (<see cref="VehicleInput.DirectSteer"/>, rotation/sensitivity,
///     dead zone, linearity), the pad stick as is (dead zone, curve), keys ramped in (3/s) and back (6/s).
///     H-shifter: once a gear button has been used, no gear held = neutral.
/// </summary>
public sealed class DriverInput(ControlSettings cfg)
{
    private static readonly Control[] All = Enum.GetValues<Control>(),
        Gears = [Control.GearR, Control.Gear1, Control.Gear2, Control.Gear3, Control.Gear4, Control.Gear5, Control.Gear6];
    private readonly bool[] _down = new bool[All.Length], _was = new bool[All.Length];
    /// <summary>Wheel steering when another device took over: the wheel claims the steering back only when turned away from it.</summary>
    private float _wheelAnchor = float.NaN;

    public ControlSettings Settings => cfg;
    /// <summary>
    ///     The pad this driver reads (split screen: each player their own, <see cref="InputSnapshot.Pads"/>); null = every pad
    ///     merged (<see cref="InputSnapshot.Gamepad"/>). A function returning null: no pad.
    /// </summary>
    public Func<InputSnapshot, GamepadState?>? PadOf { get; init; }
    public DeviceKind Active { get; private set; } = DeviceKind.Keyboard;
    public float Steer { get; private set; }
    /// <summary>Wheel steering before the clamp: beyond ±1 the wheel is past the game's lock (force feedback pushes back).</summary>
    public float SteerBeyond { get; private set; }
    public bool DirectSteer { get; private set; }
    public float Throttle { get; private set; }
    public float Brake { get; private set; }
    public float Clutch { get; private set; }
    public bool Handbrake => Down(Control.Handbrake);
    /// <summary>Gear the H-shifter asks for (0 = neutral, −1 = R), null while it is not used.</summary>
    public int? HGear { get; private set; }
    /// <summary>The wheel device in use (null: none connected).</summary>
    public JoystickState? Wheel { get; private set; }

    public bool Down(Control c) => _down[(int)c];
    public bool Pressed(Control c) => _down[(int)c] && !_was[(int)c];

    /// <summary>The joystick named in the settings, else the first one SDL calls a wheel, else the first that is not a pad.</summary>
    public static JoystickState? FindWheel(InputSnapshot input, string? name) =>
        input.Joysticks.FirstOrDefault(j => j.Name == name) ?? input.Joysticks.FirstOrDefault(j => j.IsWheel)
        ?? input.Joysticks.FirstOrDefault(j => !j.IsGameController);

    /// <summary>0..1 of one binding (pad bindings on <paramref name="pad"/>, none without one).</summary>
    public static float Value(Bind b, InputSnapshot input, JoystickState? wheel, GamepadState? pad) => b.Source switch
    {
        Source.Key => input.Keyboard.IsKeyDown((Key)b.Code) ? 1 : 0,
        Source.PadButton => pad?.IsButtonDown((GamepadButton)b.Code) == true ? 1 : 0,
        Source.PadAxis => pad == null ? 0 : b.AxisValue(pad.RawAxis((GamepadAxis)b.Code)),
        Source.JoyButton => wheel?.Button(b.Code) == true ? 1 : 0,
        Source.JoyAxis => wheel == null ? 0 : b.AxisValue(wheel.Axis(b.Code)),
        Source.JoyHat => wheel != null && (wheel.Hat(b.Code) & b.Dir) != 0 ? 1 : 0,
        _ => 0,
    };

    /// <summary>Button or hat binding of the wheel pressed this frame (menus).</summary>
    public static bool Pressed(Bind b, JoystickState w) =>
        b.Source == Source.JoyButton ? w.ButtonPressed(b.Code) : b.Source == Source.JoyHat && w.HatPressed(b.Code, b.Dir);

    /// <summary>Dead zone <paramref name="dz"/> (rescaled after it) and power curve <paramref name="exponent"/> on |x|, sign kept.</summary>
    public static float Shape(float x, float dz, float exponent)
    {
        var a = MathF.Abs(x);
        if (a >= 1) return x; // past the lock stays linear (soft lock)
        return a <= dz ? 0 : MathF.CopySign(MathF.Pow((a - dz) / (1 - dz), exponent), x);
    }

    /// <summary>Strongest value of <paramref name="c"/> on device <paramref name="d"/>.</summary>
    public float Analog(InputSnapshot input, DeviceKind d, Control c)
    {
        var b = cfg.Get(d, c);
        var pad = PadOf != null ? PadOf(input) : input.Gamepad;
        return MathF.Max(Value(b[0], input, Wheel, pad), Value(b[1], input, Wheel, pad));
    }

    private bool AnyAxis(DeviceKind d, Control c) => cfg.Get(d, c) is var b && (b[0].IsAxis || b[1].IsAxis);

    /// <summary>Wheel pedal with dead zone and inversion.</summary>
    private float Pedal(InputSnapshot input, Control c, bool invert)
    {
        var v = Analog(input, DeviceKind.Wheel, c);
        if (invert && AnyAxis(DeviceKind.Wheel, c)) v = 1 - v;
        return Shape(v, cfg.PedalDeadzone, 1);
    }

    /// <summary>Steering of the wheel: −1..1 at the game's lock, more past it.</summary>
    public float WheelSteer(InputSnapshot input)
    {
        var raw = Analog(input, DeviceKind.Wheel, Control.SteerRight) - Analog(input, DeviceKind.Wheel, Control.SteerLeft);
        var x = raw * cfg.Rotation / cfg.FullLockDegrees;
        return Shape(cfg.InvertSteer ? -x : x, cfg.SteerDeadzone, cfg.SteerLinearity);
    }

    public void Update(InputSnapshot input, float dt)
    {
        Wheel = FindWheel(input, cfg.WheelName);
        Array.Copy(_down, _was, _down.Length);
        bool key = false, pad = false, wheel = false;
        foreach (var c in All)
        {
            var on = false;
            foreach (var d in (ReadOnlySpan<DeviceKind>)[DeviceKind.Keyboard, DeviceKind.Pad, DeviceKind.Wheel])
            {
                var v = Analog(input, d, c);
                if (d == DeviceKind.Wheel && c is Control.Throttle or Control.Brake or Control.Clutch)
                    v = Pedal(input, c, c switch { Control.Throttle => cfg.InvertThrottle, Control.Brake => cfg.InvertBrake, _ => cfg.InvertClutch });
                if (d == DeviceKind.Pad && AnyAxis(d, c)) v = Shape(v, cfg.PadDeadzone, 1);
                if (v <= 0.5f || (d == DeviceKind.Wheel && c is Control.SteerLeft or Control.SteerRight)) continue; // wheel steering: by motion, below
                on = true;
                if (c is Control.SteerLeft or Control.SteerRight) // the steering device is the one steered with last
                    (key, pad, wheel) = (key || d == DeviceKind.Keyboard, pad || d == DeviceKind.Pad, wheel || d == DeviceKind.Wheel);
            }
            _down[(int)c] = on;
        }

        var hasWheelSteer = Wheel != null && AnyAxis(DeviceKind.Wheel, Control.SteerRight);
        var ws = hasWheelSteer ? WheelSteer(input) : 0;
        var stick = Shape(Analog(input, DeviceKind.Pad, Control.SteerRight) - Analog(input, DeviceKind.Pad, Control.SteerLeft), cfg.PadDeadzone, cfg.PadLinearity);
        pad |= stick != 0; // any stick motion past the dead zone, not only past half
        // a bumped wheel (a few degrees) takes nothing over: it has to be turned 0.1 of the lock away from where it was left
        if (!hasWheelSteer || Active != DeviceKind.Wheel && float.IsNaN(_wheelAnchor)) _wheelAnchor = hasWheelSteer ? ws : float.NaN;
        if (hasWheelSteer && Active != DeviceKind.Wheel && MathF.Abs(ws - _wheelAnchor) > 0.1f) wheel = true;
        if (key) Active = DeviceKind.Keyboard;
        else if (pad) Active = DeviceKind.Pad;
        else if (wheel) Active = DeviceKind.Wheel;
        if (Active == DeviceKind.Wheel) _wheelAnchor = float.NaN; // re-anchored when another device takes over

        // pedals: the most pressed of all devices (digital bindings count fully)
        float Max(Control c) => MathF.Max(MathF.Max(Analog(input, DeviceKind.Keyboard, c),
            Shape(Analog(input, DeviceKind.Pad, c), AnyAxis(DeviceKind.Pad, c) ? cfg.PadDeadzone : 0, 1)), Pedal(input, c, c switch
            {
                Control.Throttle => cfg.InvertThrottle, Control.Brake => cfg.InvertBrake, _ => cfg.InvertClutch,
            }));
        (Throttle, Brake, Clutch) = (Max(Control.Throttle), Max(Control.Brake), Max(Control.Clutch));

        DirectSteer = Active == DeviceKind.Wheel && hasWheelSteer;
        SteerBeyond = DirectSteer ? ws : 0;
        if (DirectSteer) Steer = Math.Clamp(ws, -1, 1);
        else if (stick != 0) Steer = stick;
        else
        {
            float target = (Down(Control.SteerRight) ? 1 : 0) - (Down(Control.SteerLeft) ? 1 : 0);
            var rate = (target == 0 || MathF.Sign(target) != MathF.Sign(Steer) ? 6 : 3) * dt;
            Steer += Math.Clamp(target - Steer, -rate, rate);
        }

        var held = Array.FindIndex(Gears, Down);
        if (held >= 0) HGear = held == 0 ? -1 : held;
        else if (HGear != null) HGear = 0;
        if (Pressed(Control.ShiftUp) || Pressed(Control.ShiftDown)) HGear = null; // back to sequential
    }

    /// <summary>One-shot gear change for the car: sequential up/down, or towards the H-shifter's gear (manual only).</summary>
    public int Shift(Vehicle car)
    {
        if (car.AutomaticGearbox) return 0;
        if (HGear is { } g) return g - car.Gear;
        return Pressed(Control.ShiftUp) ? 1 : Pressed(Control.ShiftDown) ? -1 : 0;
    }

    public VehicleInput Vehicle(int shift) => new(Throttle, Brake, Steer, Handbrake, shift, Clutch, DirectSteer);
}
