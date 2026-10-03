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
public sealed class GamepadState
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