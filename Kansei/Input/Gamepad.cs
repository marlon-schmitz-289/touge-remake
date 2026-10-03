using Silk.NET.SDL;

namespace Kansei.Input;

/// <summary>
///     Engine-owned gamepad button enum (portable public surface; game code never references
///     <c>Silk.NET.SDL</c>). Values come from the SDL controller buttons so the cast in
///     <see cref="GamepadState"/> / <see cref="InputAction"/> is exact.
/// </summary>
public enum GamepadButton
{
    A = GameControllerButton.A,
    B = GameControllerButton.B,
    X = GameControllerButton.X,
    Y = GameControllerButton.Y,
    Back = GameControllerButton.Back,
    Guide = GameControllerButton.Guide,
    Start = GameControllerButton.Start,
    LeftStick = GameControllerButton.Leftstick,
    RightStick = GameControllerButton.Rightstick,
    LeftShoulder = GameControllerButton.Leftshoulder,
    RightShoulder = GameControllerButton.Rightshoulder,
    DpadUp = GameControllerButton.DpadUp,
    DpadDown = GameControllerButton.DpadDown,
    DpadLeft = GameControllerButton.DpadLeft,
    DpadRight = GameControllerButton.DpadRight,
    Misc1 = GameControllerButton.Misc1,
    Paddle1 = GameControllerButton.Paddle1,
    Paddle2 = GameControllerButton.Paddle2,
    Paddle3 = GameControllerButton.Paddle3,
    Paddle4 = GameControllerButton.Paddle4,
    Touchpad = GameControllerButton.Touchpad
}

/// <summary>Engine-owned gamepad axis enum. Values come from the SDL controller axes.</summary>
public enum GamepadAxis
{
    LeftX = GameControllerAxis.Leftx,
    LeftY = GameControllerAxis.Lefty,
    RightX = GameControllerAxis.Rightx,
    RightY = GameControllerAxis.Righty,
    TriggerLeft = GameControllerAxis.Triggerleft,
    TriggerRight = GameControllerAxis.Triggerright
}
