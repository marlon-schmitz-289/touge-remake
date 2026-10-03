namespace Kansei.Input;

/// <summary>
///     Maps named actions to input bindings. Game code queries actions, not raw keys.
///     Supports rebinding at runtime for settings menus.
/// </summary>
public sealed class InputMap
{
    private readonly Dictionary<string, InputAction> _actions = new();

    /// <summary>
    ///     All defined actions.
    /// </summary>
    public IEnumerable<InputAction> Actions => _actions.Values;

    /// <summary>
    ///     Defines a new action and returns it for fluent binding setup.
    /// </summary>
    public InputAction Define(string name)
    {
        var action = new InputAction(name);
        _actions[name] = action;
        return action;
    }

    /// <summary>
    ///     Gets an action by name for rebinding.
    /// </summary>
    public InputAction? GetAction(string name)
    {
        return _actions.TryGetValue(name, out var action) ? action : null;
    }

    /// <summary>
    ///     True if any binding for this action is currently held down.
    /// </summary>
    public bool IsDown(string name, InputSnapshot input)
    {
        return _actions.TryGetValue(name, out var action) && EvalDown(action, input);
    }

    /// <summary>
    ///     True on the frame the action was first pressed.
    /// </summary>
    public bool IsPressed(string name, InputSnapshot input)
    {
        return _actions.TryGetValue(name, out var action) && EvalPressed(action, input);
    }

    /// <summary>
    ///     True on the frame the action was released.
    /// </summary>
    public bool IsReleased(string name, InputSnapshot input)
    {
        return _actions.TryGetValue(name, out var action) && EvalReleased(action, input);
    }

    /// <summary>
    ///     Returns the axis value for this action (0 for button bindings, -1..1 for axis bindings).
    ///     For key bindings, returns 1.0 if down, 0.0 if up.
    /// </summary>
    public float GetAxis(string name, InputSnapshot input)
    {
        if (!_actions.TryGetValue(name, out var action)) return 0f;

        foreach (var binding in action.Bindings)
        {
            var value = binding switch
            {
                KeyBinding kb => input.Keyboard.IsKeyDown(kb.Key) ? 1f : 0f,
                GamepadAxisBinding ab => input.Gamepad.GetAxis(ab.Axis),
                GamepadButtonBinding gb => input.Gamepad.IsButtonDown(gb.Button) ? 1f : 0f,
                MouseButtonBinding mb => input.Mouse.IsButtonDown(mb.Button) ? 1f : 0f,
                _ => 0f
            };

            if (MathF.Abs(value) > 0f) return value;
        }

        return 0f;
    }

    private static bool EvalDown(InputAction action, InputSnapshot input)
    {
        foreach (var binding in action.Bindings)
        {
            var down = binding switch
            {
                KeyBinding kb => input.Keyboard.IsKeyDown(kb.Key),
                GamepadButtonBinding gb => input.Gamepad.IsButtonDown(gb.Button),
                // Threshold's sign picks the direction: negative thresholds (e.g. MoveUp on
                // Lefty) fire when the axis is pushed that far negative, positive thresholds
                // fire pushed that far positive. Comparing absolute values here was the bug —
                // it made e.g. MoveUp and MoveDown fire together on ANY strong deflection of
                // the same axis (either direction), always canceling out net movement to zero.
                GamepadAxisBinding ab => ab.Threshold < 0
                    ? input.Gamepad.GetAxis(ab.Axis) <= ab.Threshold
                    : input.Gamepad.GetAxis(ab.Axis) >= ab.Threshold,
                MouseButtonBinding mb => input.Mouse.IsButtonDown(mb.Button),
                _ => false
            };

            if (down) return true;
        }

        return false;
    }

    private static bool EvalPressed(InputAction action, InputSnapshot input)
    {
        foreach (var binding in action.Bindings)
        {
            var pressed = binding switch
            {
                KeyBinding kb => input.Keyboard.IsKeyPressed(kb.Key),
                GamepadButtonBinding gb => input.Gamepad.IsButtonPressed(gb.Button),
                MouseButtonBinding mb => input.Mouse.IsButtonPressed(mb.Button),
                _ => false
            };

            if (pressed) return true;
        }

        return false;
    }

    private static bool EvalReleased(InputAction action, InputSnapshot input)
    {
        foreach (var binding in action.Bindings)
        {
            var released = binding switch
            {
                KeyBinding kb => input.Keyboard.IsKeyReleased(kb.Key),
                GamepadButtonBinding gb => input.Gamepad.IsButtonReleased(gb.Button),
                MouseButtonBinding mb => input.Mouse.IsButtonReleased(mb.Button),
                _ => false
            };

            if (released) return true;
        }

        return false;
    }
}