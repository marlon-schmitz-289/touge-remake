using Silk.NET.SDL;

namespace Kansei.Input;

public sealed class KeyboardState
{
    public const float RepeatDelay = 0.35f;
    public const float RepeatInterval = 0.035f;
    private readonly HashSet<KeyCode> _current = new();

    // ── Key repeat ────────────────────────────────────────────────
    private readonly Dictionary<KeyCode, float> _holdTimers = new();
    private readonly HashSet<KeyCode> _previous = new();

    /// <summary>Keys newly pressed this frame (current \ previous).</summary>
    public IEnumerable<KeyCode> PressedThisFrame
    {
        get
        {
            foreach (var k in _current)
                if (!_previous.Contains(k))
                    yield return k;
        }
    }

    public bool IsKeyDown(KeyCode key)
    {
        return _current.Contains(key);
    }

    public bool IsKeyUp(KeyCode key)
    {
        return !_current.Contains(key);
    }

    /// <summary>True if pressed this frame OR repeating from hold.</summary>
    public bool IsKeyRepeating(KeyCode key, float dt)
    {
        if (IsKeyPressed(key))
        {
            _holdTimers[key] = 0f;
            return true;
        }

        if (!IsKeyDown(key))
        {
            _holdTimers.Remove(key);
            return false;
        }

        if (!_holdTimers.TryGetValue(key, out var timer)) return false;
        timer += dt;
        _holdTimers[key] = timer;
        if (timer >= RepeatDelay)
        {
            _holdTimers[key] = timer - RepeatInterval;
            return true;
        }

        return false;
    }

    public bool IsKeyPressed(KeyCode key)
    {
        return _current.Contains(key) && !_previous.Contains(key);
    }

    public bool IsKeyReleased(KeyCode key)
    {
        return !_current.Contains(key) && _previous.Contains(key);
    }

    // ── Engine-owned Key overloads (portable; no Silk.NET.SDL in game code) ──
    // Key values are the SDL keycodes, so the cast is exact.
    public bool IsKeyDown(Key key) => IsKeyDown((KeyCode)key);
    public bool IsKeyUp(Key key) => IsKeyUp((KeyCode)key);
    public bool IsKeyPressed(Key key) => IsKeyPressed((KeyCode)key);
    public bool IsKeyReleased(Key key) => IsKeyReleased((KeyCode)key);
    public bool IsKeyRepeating(Key key, float dt) => IsKeyRepeating((KeyCode)key, dt);

    internal void BeginFrame()
    {
        _previous.Clear();
        foreach (var key in _current)
            _previous.Add(key);
    }

    internal void OnKeyDown(KeyCode key)
    {
        _current.Add(key);
    }

    internal void OnKeyUp(KeyCode key)
    {
        _current.Remove(key);
    }
}