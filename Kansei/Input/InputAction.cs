using Silk.NET.SDL;

namespace Kansei.Input;

/// <summary>
///     A single input binding — either a key, mouse button, gamepad button, or gamepad axis.
/// </summary>
public abstract record InputBinding;

public sealed record KeyBinding(KeyCode Key) : InputBinding;

public sealed record MouseButtonBinding(int Button) : InputBinding;

public sealed record GamepadButtonBinding(GameControllerButton Button) : InputBinding;

public sealed record GamepadAxisBinding(GameControllerAxis Axis, float Threshold = 0.5f) : InputBinding;

/// <summary>
///     A named action with one or more bindings.
/// </summary>
public sealed class InputAction
{
    private readonly List<InputBinding> _bindings = new();

    public InputAction(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public IReadOnlyList<InputBinding> Bindings => _bindings;

    public InputAction Bind(KeyCode key)
    {
        _bindings.Add(new KeyBinding(key));
        return this;
    }

    public InputAction Bind(GameControllerButton button)
    {
        _bindings.Add(new GamepadButtonBinding(button));
        return this;
    }

    public InputAction BindAxis(GameControllerAxis axis, float threshold = 0.5f)
    {
        _bindings.Add(new GamepadAxisBinding(axis, threshold));
        return this;
    }

    // Engine-owned enum overloads — bind gamepad without referencing Silk.NET.SDL.
    public InputAction Bind(Key key) => Bind((KeyCode)key);
    public InputAction Bind(GamepadButton button) => Bind((GameControllerButton)button);
    public InputAction BindAxis(GamepadAxis axis, float threshold = 0.5f)
        => BindAxis((GameControllerAxis)axis, threshold);

    public InputAction BindMouse(int button)
    {
        _bindings.Add(new MouseButtonBinding(button));
        return this;
    }

    public void ClearBindings()
    {
        _bindings.Clear();
    }

    public void RemoveBinding(InputBinding binding)
    {
        _bindings.Remove(binding);
    }

    public void ReplaceBinding(int index, InputBinding binding)
    {
        if (index >= 0 && index < _bindings.Count)
            _bindings[index] = binding;
    }

    /// <summary>Replaces the first KeyBinding (preserves gamepad/mouse bindings). Adds if none exists.</summary>
    public void ReplacePrimaryKey(KeyCode key)
    {
        for (var i = 0; i < _bindings.Count; i++)
            if (_bindings[i] is KeyBinding)
            {
                _bindings[i] = new KeyBinding(key);
                return;
            }

        _bindings.Insert(0, new KeyBinding(key));
    }

    public KeyCode? GetPrimaryKey()
    {
        foreach (var b in _bindings)
            if (b is KeyBinding kb)
                return kb.Key;
        return null;
    }

    /// <summary>Replaces the first GamepadButtonBinding (preserves key/mouse bindings). Adds if none exists.</summary>
    public void ReplacePrimaryGamepadButton(GameControllerButton button)
    {
        for (var i = 0; i < _bindings.Count; i++)
            if (_bindings[i] is GamepadButtonBinding)
            {
                _bindings[i] = new GamepadButtonBinding(button);
                return;
            }

        _bindings.Add(new GamepadButtonBinding(button));
    }

    public GameControllerButton? GetPrimaryGamepadButton()
    {
        foreach (var b in _bindings)
            if (b is GamepadButtonBinding gb)
                return gb.Button;
        return null;
    }

    public int? GetPrimaryMouseButton()
    {
        foreach (var b in _bindings)
            if (b is MouseButtonBinding mb)
                return mb.Button;
        return null;
    }

    /// <summary>
    ///     First gamepad-relevant binding, button OR axis (e.g. UseTool's trigger-axis bind has
    ///     no <see cref="GamepadButtonBinding" /> at all) — the single thing to show a gamepad
    ///     prompt for. Null if the action has no gamepad binding of either kind.
    /// </summary>
    public InputBinding? GetPrimaryGamepadBinding()
    {
        foreach (var b in _bindings)
            if (b is GamepadButtonBinding or GamepadAxisBinding)
                return b;
        return null;
    }
}