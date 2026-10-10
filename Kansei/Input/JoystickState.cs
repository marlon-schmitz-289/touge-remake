using Silk.NET.SDL;

namespace Kansei.Input;

/// <summary>
///     A raw joystick (SDL joystick API, not the game-controller mapping): racing wheels, pedal sets, shifters and any pad,
///     with axes −1..1 (SDL's ±32767), buttons and hats (bits 1 up, 2 right, 4 down, 8 left). Wheels are not game
///     controllers to SDL, so this is the only way to read them. A device made with the public constructor is virtual (tests,
///     --sim-wheel): its state is driven through <see cref="SetAxis"/>/<see cref="SetButton"/>/<see cref="SetHat"/>.
///     Force feedback: <see cref="Force"/> is a constant force on the steering axis through SDL haptics (−1 left … +1 right),
///     where the device supports it (<see cref="HasForceFeedback"/>); <see cref="Rumble"/> uses the joystick rumble motors.
/// </summary>
public sealed unsafe class JoystickState
{
    private readonly float[] _axes;
    private readonly bool[] _buttons, _previous;
    private readonly byte[] _hats, _previousHats;
    private readonly Sdl? _sdl;
    private Joystick* _joystick;
    private Haptic* _haptic;
    private int _effect = -1, _sine = -1;
    private short _level, _vibration;
    private ushort _period;
    private bool _updateFailed;

    public JoystickState(string name, int axes, int buttons, int hats, bool wheel = false)
    {
        (Name, IsWheel) = (name, wheel);
        (_axes, _buttons, _previous, _hats, _previousHats) = (new float[axes], new bool[buttons], new bool[buttons], new byte[hats], new byte[hats]);
        InstanceId = -1;
    }

    internal JoystickState(Sdl sdl, Joystick* joystick, bool isController)
        : this(sdl.JoystickNameS(joystick) ?? "Joystick", sdl.JoystickNumAxes(joystick), sdl.JoystickNumButtons(joystick), sdl.JoystickNumHats(joystick),
            sdl.JoystickGetType(joystick) == JoystickType.Wheel)
    {
        _sdl = sdl;
        _joystick = joystick;
        IsGameController = isController;
        InstanceId = sdl.JoystickInstanceID(joystick);
        (Vendor, Product) = (sdl.JoystickGetVendor(joystick), sdl.JoystickGetProduct(joystick));
        for (var i = 0; i < _axes.Length; i++) _axes[i] = sdl.JoystickGetAxis(joystick, i) / 32767f; // pedals rest at ±1, not 0
        OpenHaptic();
    }

    public string Name { get; }
    /// <summary>SDL joystick instance id; −1 for a virtual device.</summary>
    public int InstanceId { get; }
    /// <summary>USB vendor/product id (0 when unknown), to recognise a wheel model.</summary>
    public ushort Vendor { get; init; }
    public ushort Product { get; init; }
    /// <summary>Also open as a game controller (<see cref="GamepadState"/>): an ordinary pad, not a wheel.</summary>
    public bool IsGameController { get; }
    /// <summary>SDL reports the device type as a wheel.</summary>
    public bool IsWheel { get; }
    public bool HasForceFeedback => _effect >= 0;
    /// <summary>The wheel takes a sine effect alongside the constant force (<see cref="SetVibration"/>).</summary>
    public bool HasVibration => _sine >= 0;
    public bool Connected { get; internal set; } = true;
    /// <summary>Last force sent (−1..1, + = right), also on devices without force feedback (input debug).</summary>
    public float Force { get; private set; }

    public int AxisCount => _axes.Length;
    public int ButtonCount => _buttons.Length;
    public int HatCount => _hats.Length;
    public float Axis(int i) => (uint)i < (uint)_axes.Length ? _axes[i] : 0;
    public bool Button(int i) => (uint)i < (uint)_buttons.Length && _buttons[i];
    public bool ButtonPressed(int i) => Button(i) && !_previous[i];
    public byte Hat(int i) => (uint)i < (uint)_hats.Length ? _hats[i] : (byte)0;
    /// <summary>Hat direction bit <paramref name="bit"/> newly set this frame.</summary>
    public bool HatPressed(int i, int bit) => (Hat(i) & bit) != 0 && (_previousHats[i] & bit) == 0;

    public void SetAxis(int i, float v)
    {
        if ((uint)i < (uint)_axes.Length) _axes[i] = Math.Clamp(v, -1, 1);
    }

    public void SetButton(int i, bool down)
    {
        if ((uint)i < (uint)_buttons.Length) _buttons[i] = down;
    }

    public void SetHat(int i, byte bits)
    {
        if ((uint)i < (uint)_hats.Length) _hats[i] = bits;
    }

    /// <summary>Copies the current buttons/hats to the previous frame's (pressed edges); the snapshot calls it, a test between frames.</summary>
    public void BeginFrame()
    {
        Array.Copy(_buttons, _previous, _buttons.Length);
        Array.Copy(_hats, _previousHats, _hats.Length);
    }

    /// <summary>Constant force −1..1 (+ = turn right) on the steering axis; only sent when it changes.</summary>
    public void SetForce(float force)
    {
        Force = Math.Clamp(force, -1, 1);
        if (_effect < 0) return;
        // SDL/DirectInput: a positive level on the steering axis is a force from the right (pushes left) – the sign is flipped here
        var level = (short)MathF.Round(-Force * 32767);
        if (level == _level) return;
        _level = level;
        var e = ConstantEffect(level);
        if (_sdl!.HapticUpdateEffect(_haptic, _effect, &e) < 0 && !_updateFailed)
        {
            _updateFailed = true; // logged once
            Console.WriteLine($"[Kansei] {Name}: force feedback update failed ({_sdl.GetErrorS()})");
        }
    }

    /// <summary>
    ///     Sine vibration on the steering axis, <paramref name="amplitude"/> 0..1 at <paramref name="hz"/>, as its own effect next to
    ///     the constant force: drivers that smooth or rate-limit constant-force updates (Linux) lose a vibration sent through those.
    /// </summary>
    public void SetVibration(float amplitude, float hz)
    {
        if (_sine < 0) return;
        var magnitude = (short)MathF.Round(Math.Clamp(amplitude, 0, 1) * 32767);
        var period = (ushort)Math.Clamp(MathF.Round(1000 / MathF.Max(hz, 1)), 20, 1000);
        // a re-upload restarts the wave on some drivers: only for a clear change
        if (Math.Abs(magnitude - _vibration) < 600 && Math.Abs(period - _period) < 4 && (magnitude == 0) == (_vibration == 0)) return;
        (_vibration, _period) = (magnitude, period);
        var e = SineEffect(magnitude, period);
        _sdl!.HapticUpdateEffect(_haptic, _sine, &e);
    }

    /// <summary>Rumble motors 0..1 for <paramref name="ms"/> (pads and some wheels; no-op elsewhere).</summary>
    public void Rumble(float low, float high, uint ms = 100)
    {
        if (_joystick != null)
            _sdl!.JoystickRumble(_joystick, (ushort)(Math.Clamp(low, 0, 1) * 65535), (ushort)(Math.Clamp(high, 0, 1) * 65535), ms);
    }

    private static HapticEffect ConstantEffect(short level)
    {
        var e = new HapticEffect { Type = Sdl.HapticConstant };
        e.Constant.Type = Sdl.HapticConstant;
        e.Constant.Direction.Type = (byte)Sdl.HapticSteeringAxis;
        e.Constant.Length = Sdl.HapticInfinity;
        e.Constant.Level = level;
        return e;
    }

    private static HapticEffect SineEffect(short magnitude, ushort period)
    {
        var e = new HapticEffect { Type = Sdl.HapticSine };
        e.Periodic.Type = Sdl.HapticSine;
        e.Periodic.Direction.Type = (byte)Sdl.HapticSteeringAxis;
        e.Periodic.Length = Sdl.HapticInfinity;
        e.Periodic.Period = period;
        e.Periodic.Magnitude = magnitude;
        return e;
    }

    /// <summary>
    ///     SDL haptics on the joystick: a constant-force effect that runs forever and is updated per frame (the usual way for
    ///     wheels); the wheel's own spring (autocenter) is switched off so the game's forces are the only ones.
    /// </summary>
    private void OpenHaptic()
    {
        if (_sdl!.JoystickIsHaptic(_joystick) != 1) return;
        _haptic = _sdl.HapticOpenFromJoystick(_joystick);
        if (_haptic == null) return;
        var features = _sdl.HapticQuery(_haptic);
        if ((features & Sdl.HapticConstant) == 0)
        {
            _sdl.HapticClose(_haptic);
            _haptic = null;
            return;
        }
        if ((features & Sdl.HapticAutocenter) != 0)
            Console.WriteLine($"[Kansei] {Name}: autocenter off -> {_sdl.HapticSetAutocenter(_haptic, 0)}");
        if ((features & Sdl.HapticGain) != 0) _sdl.HapticSetGain(_haptic, 100);
        var e = ConstantEffect(0);
        _effect = _sdl.HapticNewEffect(_haptic, &e);
        if (_effect >= 0) _sdl.HapticRunEffect(_haptic, _effect, Sdl.HapticInfinity); // SDL's Length ms→µs wraps 32-bit (~71 min); infinite iterations do not
        Console.WriteLine($"[Kansei] {Name}: force feedback {(_effect >= 0 ? "on" : $"not available ({_sdl.GetErrorS()})")}");
        if (_effect < 0 || (features & Sdl.HapticSine) == 0) return;
        var sine = SineEffect(0, 100);
        _sine = _sdl.HapticNewEffect(_haptic, &sine);
        if (_sine >= 0) _sdl.HapticRunEffect(_haptic, _sine, Sdl.HapticInfinity);
    }

    internal void Close()
    {
        Connected = false;
        if (_haptic != null) _sdl!.HapticClose(_haptic);
        if (_joystick != null) _sdl!.JoystickClose(_joystick);
        _haptic = null;
        _joystick = null;
        _effect = _sine = -1;
    }

    internal void OnAxis(int axis, short value) => SetAxis(axis, value / 32767f);
}
