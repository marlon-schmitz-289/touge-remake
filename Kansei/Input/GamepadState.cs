using System.Numerics;
using Silk.NET.SDL;

namespace Kansei.Input;

/// <summary>
///     Raw per-frame gamepad state. Purely local to this peer — never read directly by ECS
///     systems (<c>MEFactory/ECS/Systems/*.cs</c>). Gameplay code goes through
///     <c>InputMap.IsPressed/IsDown(action, input)</c> just like keyboard/mouse, which is what
///     gets enqueued into <c>ActionBuffer</c>/replayed by <c>ActionProcessor</c> — that's the
///     only path that has to stay identical across multiplayer peers. Reading this class
///     directly from a system would still be 100% deterministic *for that peer*, but it would
///     bypass the buffer/replay pipeline other peers rely on to see the same action.
/// </summary>
public sealed unsafe class GamepadState
{
    private const float DeadZone = 0.15f;
    private const float AxisMax = 32767f;
    private readonly Dictionary<GameControllerAxis, float> _axes = new();

    private readonly Dictionary<GameControllerButton, bool> _current = new();
    private readonly Dictionary<GameControllerButton, bool> _previous = new();

    public bool IsConnected { get; internal set; }

    /// <summary>Buttons newly pressed this frame (current \ previous) — for rebind-capture UI.</summary>
    public IEnumerable<GameControllerButton> PressedThisFrame
    {
        get
        {
            foreach (var (button, down) in _current)
                if (down && (!_previous.TryGetValue(button, out var prev) || !prev))
                    yield return button;
        }
    }

    public float GetAxis(GameControllerAxis axis)
    {
        if (!_axes.TryGetValue(axis, out var value)) return 0f;
        return MathF.Abs(value) < DeadZone ? 0f : value;
    }

    public bool IsButtonDown(GameControllerButton button)
    {
        return _current.TryGetValue(button, out var v) && v;
    }

    public bool IsButtonPressed(GameControllerButton button)
    {
        return _current.TryGetValue(button, out var cur) && cur &&
               (!_previous.TryGetValue(button, out var prev) || !prev);
    }

    public bool IsButtonReleased(GameControllerButton button)
    {
        return (!_current.TryGetValue(button, out var cur) || !cur) &&
               _previous.TryGetValue(button, out var prev) && prev;
    }

    // ── Engine-owned enum overloads (portable; no Silk.NET.SDL in game code) ──
    public bool IsButtonDown(GamepadButton button) => IsButtonDown((GameControllerButton)button);
    public bool IsButtonPressed(GamepadButton button) => IsButtonPressed((GameControllerButton)button);
    public bool IsButtonReleased(GamepadButton button) => IsButtonReleased((GameControllerButton)button);
    public float GetAxis(GamepadAxis axis) => GetAxis((GameControllerAxis)axis);
    /// <summary>Axis without the fixed dead zone (the game applies its own, Options → Controls).</summary>
    public float RawAxis(GamepadAxis axis) => _axes.GetValueOrDefault((GameControllerAxis)axis);

    // ------------------------------------------------------------ output and motion (one real pad; the merged Gamepad and device-free pads return −1)

    internal Sdl? Sdl;
    internal nint Controller;

    /// <summary>Controller name ("DualSense Wireless Controller"), empty for the merged state.</summary>
    public string Name { get; internal set; } = "";
    /// <summary>A PS5 DualSense (SDL type PS5): lightbar, adaptive triggers, mic LED, speaker, motion sensors.</summary>
    public bool IsDualSense { get; internal set; }
    /// <summary>Every output call logged with its arguments and SDL's return code (--dualsense-log).</summary>
    public static bool Trace { get; set; }
    /// <summary>Accelerometer in m/s² (SDL axes: +X right, +Y up, +Z towards the player) while <see cref="SetMotion"/> is on.</summary>
    public Vector3 Accel { get; internal set; }
    /// <summary>First finger on the touchpad (0..1, 0,0 = top left), null = none.</summary>
    public Vector2? Touch { get; internal set; }

    private int Log(string what, int rc)
    {
        if (Trace) Console.WriteLine($"[DualSense] {Name}: {what} -> rc {rc}{(rc < 0 && Sdl != null ? $" ({Sdl.GetErrorS()})" : "")}");
        return rc;
    }

    private GameController* Pad => (GameController*)Controller;

    /// <summary>Lightbar colour (SDL_GameControllerSetLED).</summary>
    public int SetLed(byte r, byte g, byte b) =>
        Sdl == null || Controller == 0 ? -1 : Log($"LED {r} {g} {b}", Sdl.GameControllerSetLED(Pad, r, g, b));

    /// <summary>Rumble motors 0..1 for <paramref name="ms"/> (SDL_GameControllerRumble).</summary>
    public int Rumble(float low, float high, uint ms)
    {
        if (Sdl == null || Controller == 0) return -1;
        ushort l = (ushort)(Math.Clamp(low, 0, 1) * 65535), h = (ushort)(Math.Clamp(high, 0, 1) * 65535);
        var rc = Sdl.GameControllerRumble(Pad, l, h, ms);
        return (l | h) != 0 ? Log($"rumble {l} {h} {ms} ms", rc) : rc; // silence (sent every frame at rest) is not logged
    }

    /// <summary>Player number on the player LEDs (0 = first), −1 = LEDs off (SDL_GameControllerSetPlayerIndex, no return code).</summary>
    public int SetPlayerIndex(int index)
    {
        if (Sdl == null || Controller == 0) return -1;
        Sdl.GameControllerSetPlayerIndex(Pad, index);
        return Log($"player LEDs {index}", 0);
    }

    /// <summary>Raw controller effect packet (DualSense: <see cref="DualSense.Effect"/>; SDL_GameControllerSendEffect).</summary>
    public int SendEffect(ReadOnlySpan<byte> effect)
    {
        if (Sdl == null || Controller == 0) return -1;
        int rc;
        fixed (byte* p = effect) rc = Sdl.GameControllerSendEffect(Pad, p, effect.Length);
        return Log($"effect {Convert.ToHexString(effect)}", rc);
    }

    /// <summary>Accelerometer reports on/off (<see cref="Accel"/>).</summary>
    public int SetMotion(bool on) =>
        Sdl == null || Controller == 0 ? -1 : Log($"motion {(on ? "on" : "off")}", Sdl.GameControllerSetSensorEnabled(Pad, SensorType.Accel, on ? SdlBool.True : SdlBool.False));

    internal void BeginFrame()
    {
        _previous.Clear();
        foreach (var kvp in _current)
            _previous[kvp.Key] = kvp.Value;
    }

    /// <summary>Clears all button/axis state — call on disconnect so a held button doesn't survive a hot-unplug.</summary>
    internal void Reset()
    {
        _current.Clear();
        _previous.Clear();
        _axes.Clear();
        (Controller, Touch) = (0, null); // closed by the snapshot: no more output to it
    }

    internal void OnButtonDown(GameControllerButton button)
    {
        _current[button] = true;
    }

    internal void OnButtonUp(GameControllerButton button)
    {
        _current[button] = false;
    }

    internal void OnAxis(GameControllerAxis axis, short value)
    {
        _axes[axis] = value / AxisMax;
    }
}