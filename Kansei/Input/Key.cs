using Silk.NET.SDL;

namespace Kansei.Input;

/// <summary>
///     Engine-owned keyboard key enum — the portable public surface so game code never has to
///     reference <c>Silk.NET.SDL</c> (which also drags in a conflicting <c>Color</c> type).
///     Values are defined from the SDL keycodes so the cast in <see cref="KeyboardState"/> is
///     correct by construction.
///     <para>ponytail: mirrors SDL keycodes to stay a zero-cost cast; if a non-SDL platform is
///     ever added, this becomes the canonical enum and the SDL layer maps into it instead.</para>
/// </summary>
public enum Key
{
    // Letters
    A = KeyCode.KA, B = KeyCode.KB, C = KeyCode.KC, D = KeyCode.KD, E = KeyCode.KE,
    F = KeyCode.KF, G = KeyCode.KG, H = KeyCode.KH, I = KeyCode.KI, J = KeyCode.KJ,
    K = KeyCode.KK, L = KeyCode.KL, M = KeyCode.KM, N = KeyCode.KN, O = KeyCode.KO,
    P = KeyCode.KP, Q = KeyCode.KQ, R = KeyCode.KR, S = KeyCode.KS, T = KeyCode.KT,
    U = KeyCode.KU, V = KeyCode.KV, W = KeyCode.KW, X = KeyCode.KX, Y = KeyCode.KY, Z = KeyCode.KZ,

    // Digits (top row)
    D0 = KeyCode.K0, D1 = KeyCode.K1, D2 = KeyCode.K2, D3 = KeyCode.K3, D4 = KeyCode.K4,
    D5 = KeyCode.K5, D6 = KeyCode.K6, D7 = KeyCode.K7, D8 = KeyCode.K8, D9 = KeyCode.K9,

    // Arrows
    Left = KeyCode.KLeft, Right = KeyCode.KRight, Up = KeyCode.KUp, Down = KeyCode.KDown,

    // Common controls
    Space = KeyCode.KSpace,
    Enter = KeyCode.KReturn,
    Escape = KeyCode.KEscape,
    Tab = KeyCode.KTab,
    Backspace = KeyCode.KBackspace,
    Delete = KeyCode.KDelete,
    LeftShift = KeyCode.KLshift, RightShift = KeyCode.KRshift,
    LeftCtrl = KeyCode.KLctrl, RightCtrl = KeyCode.KRctrl,
    LeftAlt = KeyCode.KLalt, RightAlt = KeyCode.KRalt,
    LeftGui = KeyCode.KLgui, RightGui = KeyCode.KRgui,

    // Function keys
    F1 = KeyCode.KF1, F2 = KeyCode.KF2, F3 = KeyCode.KF3, F4 = KeyCode.KF4,
    F5 = KeyCode.KF5, F6 = KeyCode.KF6, F7 = KeyCode.KF7, F8 = KeyCode.KF8,
    F9 = KeyCode.KF9, F10 = KeyCode.KF10, F11 = KeyCode.KF11, F12 = KeyCode.KF12
}
