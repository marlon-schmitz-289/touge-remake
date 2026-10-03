namespace Kansei.Input;

public sealed class MouseState
{
    private uint _current;
    private uint _previous;

    public int X { get; internal set; }
    public int Y { get; internal set; }
    public int ScrollDelta { get; internal set; }

    public bool IsButtonDown(int button)
    {
        return (_current & 1u << button) != 0;
    }

    public bool IsButtonPressed(int button)
    {
        return (_current & 1u << button) != 0 && (_previous & 1u << button) == 0;
    }

    public bool IsButtonReleased(int button)
    {
        return (_current & 1u << button) == 0 && (_previous & 1u << button) != 0;
    }

    internal void BeginFrame()
    {
        _previous = _current;
        ScrollDelta = 0;
    }

    internal void OnButtonDown(int button)
    {
        _current |= 1u << button;
    }

    internal void OnButtonUp(int button)
    {
        _current &= ~(1u << button);
    }
}