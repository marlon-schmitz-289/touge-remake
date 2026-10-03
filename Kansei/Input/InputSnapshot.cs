using System.Text;
using Silk.NET.SDL;

namespace Kansei.Input;

/// <summary>Which input device last produced meaningful input — drives cursor ownership and focus-ring visibility.</summary>
public enum InputDevice
{
    Mouse,
    Keyboard,
    Gamepad
}

public sealed unsafe class InputSnapshot
{
    private readonly Sdl _sdl;

    /// <summary>All currently open controllers, keyed by SDL joystick instance ID.</summary>
    private readonly Dictionary<int, nint> _open = new();

    private int _activeInstanceId = -1;

    internal InputSnapshot(Sdl sdl)
    {
        _sdl = sdl;
        TryOpenAny();
        PromoteActive();
    }

    /// <summary>
    ///     A snapshot with no device behind it. The three state objects are real and can be driven
    ///     directly, which is what lets a test press a key without a window, a GPU or SDL — see
    ///     <see cref="KeyboardState.OnKeyDown" />. Device polling is skipped rather than faked.
    /// </summary>
    internal InputSnapshot()
    {
        _sdl = null!;
    }

    /// <summary>False for the device-free snapshot above. Everything that talks to SDL checks this.</summary>
    private bool HasDevice => _sdl is not null;

    public KeyboardState Keyboard { get; } = new();
    public MouseState Mouse { get; } = new();
    public GamepadState Gamepad { get; } = new();

    /// <summary>Controller type of the currently active pad (Xbox/PlayStation/Switch/etc), for glyph selection.</summary>
    public GameControllerType ActiveType { get; private set; } = GameControllerType.Unknown;

    /// <summary>Display name of the currently active pad, e.g. "Xbox Wireless Controller".</summary>
    public string? ActiveName { get; private set; }

    /// <summary>Characters typed this frame (from SDL_TEXTINPUT events).</summary>
    public string TypedText { get; private set; } = "";

    /// <summary>
    ///     Which device last produced input. The cursor (<see cref="Mouse"/>'s position) is
    ///     always the real OS mouse — a gamepad never moves it — this is purely for cosmetics
    ///     like showing/hiding a keyboard/gamepad focus ring (see e.g. <c>SettingsUI</c>).
    /// </summary>
    public InputDevice LastInputDevice { get; private set; } = InputDevice.Mouse;

    private const float StickDeadZone = 0.15f;

    internal void BeginFrame()
    {
        Keyboard.BeginFrame();
        Mouse.BeginFrame();
        Gamepad.BeginFrame();
        TypedText = "";
    }

    internal void ProcessEvent(Event evt)
    {
        switch ((EventType)evt.Type)
        {
            case EventType.Keydown:
                Keyboard.OnKeyDown((KeyCode)evt.Key.Keysym.Sym);
                LastInputDevice = InputDevice.Keyboard;
                break;
            case EventType.Keyup:
                Keyboard.OnKeyUp((KeyCode)evt.Key.Keysym.Sym);
                break;
            case EventType.Textinput:
                unsafe
                {
                    var p = evt.Text.Text;
                    var len = 0;
                    while (len < 32 && p[len] != 0) len++;
                    if (len > 0)
                        TypedText += Encoding.UTF8.GetString(p, len);
                }

                break;
            case EventType.Mousemotion:
                Mouse.X = evt.Motion.X;
                Mouse.Y = evt.Motion.Y;
                LastInputDevice = InputDevice.Mouse;
                break;
            case EventType.Mousebuttondown:
                Mouse.OnButtonDown(evt.Button.Button);
                LastInputDevice = InputDevice.Mouse;
                break;
            case EventType.Mousebuttonup:
                Mouse.OnButtonUp(evt.Button.Button);
                break;
            case EventType.Mousewheel:
                Mouse.ScrollDelta = evt.Wheel.Y;
                break;
            case EventType.Controllerdeviceadded:
                OnDeviceAdded(evt.Cdevice.Which);
                break;
            case EventType.Controllerdeviceremoved:
                OnDeviceRemoved(evt.Cdevice.Which);
                break;
            case EventType.Controllerbuttondown:
                Gamepad.OnButtonDown((GameControllerButton)evt.Cbutton.Button);
                LastInputDevice = InputDevice.Gamepad;
                break;
            case EventType.Controllerbuttonup:
                Gamepad.OnButtonUp((GameControllerButton)evt.Cbutton.Button);
                break;
            case EventType.Controlleraxismotion:
                var axisValue = evt.Caxis.Value / 32767f;
                Gamepad.OnAxis((GameControllerAxis)evt.Caxis.Axis, evt.Caxis.Value);
                if (MathF.Abs(axisValue) > StickDeadZone)
                    LastInputDevice = InputDevice.Gamepad;
                break;
        }
    }

    /// <summary>Mouse position always comes from the real OS cursor — never synthesized from a gamepad.</summary>
    internal void EndFrame()
    {
        if (!HasDevice) return;

        int mx = 0, my = 0;
        _sdl.GetMouseState(ref mx, ref my);
        Mouse.X = mx;
        Mouse.Y = my;
    }

    /// <summary>Scans every currently-connected joystick and opens the ones that are game controllers.</summary>
    private void TryOpenAny()
    {
        if (!HasDevice) return;

        for (var i = 0; i < _sdl.NumJoysticks(); i++)
            if (_sdl.IsGameController(i) == SdlBool.True)
                OpenDevice(i);
    }

    /// <summary>Device-added handler. SDL passes a device *index* here (not an instance ID).</summary>
    private void OnDeviceAdded(int deviceIndex)
    {
        var instanceId = OpenDevice(deviceIndex);
        // First controller connected wins and stays active until it disconnects — simplest
        // correct policy for a single local player; extra pads just sit open, idle, as a
        // seamless hot-swap pool for OnDeviceRemoved to promote from.
        if (instanceId >= 0 && _activeInstanceId == -1)
            SetActive(instanceId);
    }

    /// <summary>Device-removed handler. SDL passes the joystick *instance ID* here (unlike add).</summary>
    private void OnDeviceRemoved(int instanceId)
    {
        if (_open.TryGetValue(instanceId, out var handle))
        {
            _sdl.GameControllerClose((GameController*)handle);
            _open.Remove(instanceId);
        }

        if (instanceId != _activeInstanceId) return;

        _activeInstanceId = -1;
        Gamepad.IsConnected = false;
        Gamepad.Reset();
        ActiveType = GameControllerType.Unknown;
        ActiveName = null;
        PromoteActive();
    }

    /// <summary>Opens a controller by device index, dedup'd by instance ID. Returns the instance ID, or -1 on failure.</summary>
    private int OpenDevice(int deviceIndex)
    {
        var controller = _sdl.GameControllerOpen(deviceIndex);
        if (controller == null) return -1;

        var instanceId = _sdl.JoystickInstanceID(_sdl.GameControllerGetJoystick(controller));
        if (_open.ContainsKey(instanceId))
        {
            // Already tracked (e.g. re-announced) — close the redundant handle.
            _sdl.GameControllerClose(controller);
            return instanceId;
        }

        _open[instanceId] = (nint)controller;
        return instanceId;
    }

    /// <summary>Promotes the first open-but-idle controller to active, if none is active yet.</summary>
    private void PromoteActive()
    {
        if (_activeInstanceId != -1 || _open.Count == 0) return;
        foreach (var instanceId in _open.Keys)
        {
            SetActive(instanceId);
            return;
        }
    }

    private void SetActive(int instanceId)
    {
        _activeInstanceId = instanceId;
        var controller = (GameController*)_open[instanceId];
        Gamepad.IsConnected = true;
        ActiveType = _sdl.GameControllerGetType(controller);
        ActiveName = _sdl.GameControllerNameS(controller);
    }
}