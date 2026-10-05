namespace Kansei.Input;

/// <summary>
///     Auto-repeat of any held digital input (D-pad, a stick past a threshold, a wheel hat) with the keyboard's timing
///     (<see cref="KeyboardState.IsKeyRepeating(KeyCode, float)"/>): true on the press, then after
///     <see cref="KeyboardState.RepeatDelay"/> every <see cref="KeyboardState.RepeatInterval"/>; releasing starts over.
/// </summary>
public sealed class HoldRepeat
{
    private float _held = -1;

    /// <summary>One frame of the input (<paramref name="down"/>: held now); true when it fires.</summary>
    public bool Update(bool down, float dt)
    {
        if (!down)
        {
            _held = -1;
            return false;
        }
        if (_held < 0)
        {
            _held = 0;
            return true;
        }
        _held += dt;
        if (_held < KeyboardState.RepeatDelay) return false;
        _held -= KeyboardState.RepeatInterval;
        return true;
    }
}
